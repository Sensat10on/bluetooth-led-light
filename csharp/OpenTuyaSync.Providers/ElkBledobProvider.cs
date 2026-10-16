using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using OpenTuyaSync.Core;
using OpenTuyaSync.Core.Ipc;

namespace OpenTuyaSync.Providers;

/// <summary>
/// Световые панели и ленты, которыми управляет приложение LotusLamp X --
/// так продаются многие клоны с Amazon/AliExpress (NBBUFF, Ledagic и
/// подобные). Bluetooth LE, подключение по MAC-адресу.
///
/// Протокол проверен на реальной ленте ELK-BLEDOB: служба FFF0,
/// характеристика записи FFF3 (WriteWithoutResponse), команды
/// подобраны верно -- лента меняет цвет и включается.
///
/// Отдельная сложность -- перечисление служб. У недорогих BLE-устройств
/// таблица GATT отдаётся нестабильно: то видна вся, то только стандартная
/// служба с именем устройства. Поэтому подключение повторяется с нуля,
/// освобождая объект устройства между попытками: иначе Windows отдаёт тот
/// же неполный список.
///
/// Соединение асинхронное (WinRT). Чтобы не превращать весь движок в
/// async, вызовы блокируются с таймаутом: отправка идёт из отдельного
/// потока, поэтому ожидание здесь никого не тормозит.
/// </summary>
public sealed class ElkBledobProvider : IColorProvider
{
    /// <summary>Характеристика для записи команд. У большинства клонов
    /// это FFF3 из стандартного 16-битного диапазона FFF0.</summary>
    private static readonly Guid WriteCharacteristic =
        new("0000fff3-0000-1000-8000-00805f9b34fb");

    /// <summary>Стандартная характеристика имени устройства. Чтение из неё
    /// заставляет Windows установить настоящую связь с устройством, после
    /// чего становится видна производительная служба FFF0.</summary>
    private static readonly Guid DeviceNameCharacteristic =
        new("00002a00-0000-1000-8000-00805f9b34fb");

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(3);

    private readonly string _mac;
    private readonly string _deviceId;

    private readonly AutoResetEvent _wake = new(false);
    private readonly object _stateSync = new();

    private BluetoothLEDevice? _device;
    private GattCharacteristic? _characteristic;
    private Thread? _worker;
    private volatile bool _stopRequested;
    private volatile bool _connected;

    private Rgb? _pendingColor;
    private bool _pendingOn, _pendingOff;

    public ElkBledobProvider(DeviceConfig config)
    {
        _deviceId = config.Id;
        var e = config.ElkBledob ?? throw new ArgumentException("Не заданы параметры ELK-BLEDOB");
        _mac = e.Mac;
    }

    public DeviceKind Kind => DeviceKind.ElkBledob;
    public string Caption => "ELK-BLEDOB / LotusLamp X";
    public bool IsConnected => _connected;

    public string StatusText => _connected
        ? $"подключено, {_mac}"
        : "не подключено";

    /// <summary>Конструктор для диагностики: тот же протокол, но без
    /// привязки к настройкам устройства.</summary>
    public ElkBledobProvider(string mac, string deviceId = "diagnostic")
    {
        _deviceId = deviceId;
        _mac = mac;
    }

    public void Connect()
    {
        _stopRequested = false;
        ConnectAsync().GetAwaiter().GetResult();
        _connected = true;

        if (_worker == null)
        {
            _worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"ble-sender-{_deviceId}",
                Priority = ThreadPriority.BelowNormal,
            };
            _worker.Start();
        }
    }

    /// <summary>
    /// Вся работа с Bluetooth вынесена в async-метод: WinRT-операции
    /// асинхронные, а остальной движок синхронный, поэтому ожидание
    /// происходит здесь, в фоновом потоке вызывающего.
    /// </summary>
    /// <summary>
    /// Сколько ждать между перезапусками Bluetooth-радио.
    ///
    /// Ограничение обязательно, и оно общее для всего процесса. Перезапуск
    /// радио разрывает ВСЕ Bluetooth-соединения компьютера, поэтому частые
    /// перезапуски превращаются в постоянные отключения чужих устройств:
    /// именно из-за них пропадал Bluetooth-пульт приставки Xiaomi. Прежде чем
    /// трогать радио, программа выжидает этот срок.
    /// </summary>
    private static readonly TimeSpan RadioRestartCooldown = TimeSpan.FromMinutes(10);

    /// <summary>Когда радио перезапускалось в последний раз.</summary>
    private static DateTime _lastRadioRestart = DateTime.MinValue;

    private static readonly object RadioLock = new();

    /// <summary>Сколько раз перезапуск радио уже пропущен из-за ограничения.</summary>
    public static int SkippedRadioRestarts { get; private set; }

    /// <summary>
    /// Можно ли сейчас перезапустить радио: с прошлого раза прошёл срок.
    ///
    /// Проверка отделена от перезапуска, потому что решение принимается до
    /// тяжёлой операции: бессмысленно пробовать, если программа только что
    /// это делала.
    /// </summary>
    public static bool CanRestartRadio()
    {
        lock (RadioLock)
        {
            return DateTime.UtcNow - _lastRadioRestart >= RadioRestartCooldown;
        }
    }

    /// <summary>
    /// Программное выключение и включение Bluetooth. Нужно для лечения
    /// залипшего кэша GATT: Windows начинает отказывать в доступе к
    /// производительной службе устройства, и перезапуск радио это
    /// исправляет.
    ///
    /// Возвращает true, если удалось. Управление радио может требовать
    /// повышенных прав -- тогда метод вернёт false, и пользователю
    /// останется сделать это вручную.
    ///
    /// Радио трогается только при явном разрешении вызывающей стороны: см.
    /// <see cref="AllowBluetoothRecovery"/> в настройках.
    /// </summary>
    public static async Task<bool> RestartBluetoothRadioAsync()
    {
        lock (RadioLock)
        {
            // Ограничение частоты: иначе каждое неудачное подключение рвало
            // бы Bluetooth у всех остальных устройств.
            if (DateTime.UtcNow - _lastRadioRestart < RadioRestartCooldown)
            {
                SkippedRadioRestarts++;
                return false;
            }

            _lastRadioRestart = DateTime.UtcNow;
        }

        try
        {
            var radios = await Windows.Devices.Radios.Radio.GetRadiosAsync();

            Windows.Devices.Radios.Radio? bluetooth = null;
            foreach (var radio in radios)
            {
                if (radio.Kind == Windows.Devices.Radios.RadioKind.Bluetooth)
                {
                    bluetooth = radio;
                    break;
                }
            }

            if (bluetooth == null) return false;

            var off = await bluetooth.SetStateAsync(Windows.Devices.Radios.RadioState.Off);
            if (off != Windows.Devices.Radios.RadioAccessStatus.Allowed) return false;

            await Task.Delay(1500);

            var on = await bluetooth.SetStateAsync(Windows.Devices.Radios.RadioState.On);
            if (on != Windows.Devices.Radios.RadioAccessStatus.Allowed) return false;

            // Даём стеку Bluetooth подняться.
            await Task.Delay(2500);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Перезапускает Bluetooth и пытается подключиться заново. Вызывается,
    /// когда Windows отказала в доступе к службе устройства.
    ///
    /// Возвращает true, если перезапуск действительно выполнялся: при
    /// ограничении частоты метод сразу возвращает false, и вызывающая
    /// сторона не должна считать, что попытка была.
    /// </summary>
    public bool ConnectWithRadioRestart()
    {
        if (!CanRestartRadio()) return false;

        bool restarted = RestartBluetoothRadioAsync().GetAwaiter().GetResult();
        if (!restarted) return false;

        Connect();
        return true;
    }

    private async Task ConnectAsync()
    {
        ulong address = ParseMac(_mac);

        // Службы у недорогих BLE-устройств перечисляются нестабильно: то
        // видна вся таблица, то только стандартная служба с именем. Причина
        // в том, что лента отключает радио, когда с ней никто не работает,
        // и производительная служба FFF0 пропадает из выдачи до
        // установления связи. Поэтому подключение повторяется с нуля.
        //
        // Попыток немного: каждая неудачная упирается во внутренний
        // тайм-аут Windows, и при десяти попытках подключение растягивалось
        // почти на тридцать секунд. Для залипшего кэша GATT повторы всё
        // равно не помогают -- там нужен перезапуск Bluetooth.
        Exception? lastError = null;
        string lastAvailable = "";

        const int maxAttempts = 4;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                await DisposeDeviceAsync(300 + attempt * 200);

                _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address)
                    ?? throw new InvalidOperationException($"Устройство {_mac} не найдено");

                _device.ConnectionStatusChanged += (_, _) =>
                {
                    // Событие приходит из потока WinRT в момент, когда
                    // устройство может быть уже освобождено. Без защиты
                    // исключение здесь уходит в необработанные и роняет
                    // весь процесс службы.
                    try { _connectionState = _device?.ConnectionStatus.ToString() ?? "освобождено"; }
                    catch (Exception) { }
                };

                var services = await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached);

                if (services.Status != GattCommunicationStatus.Success)
                {
                    lastError = new InvalidOperationException(
                        $"Не удалось получить службы: {services.Status}");
                    await Task.Delay(400);
                    continue;
                }

                var available = new List<string>();
                GattCharacteristic? found = null;
                GattCharacteristic? nameCharacteristic = null;
                bool accessDenied = false;

                foreach (var service in services.Services)
                {
                    var characteristics = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);

                    // Отказ в доступе к характеристикам службы -- признак
                    // залипшего кэша GATT в Windows. Связь при этом
                    // формально установлена, но производительная служба
                    // остаётся закрытой. Лечится перезапуском службы
                    // Bluetooth; из приложения это сделать нельзя, поэтому
                    // запоминаем признак, чтобы выдать понятное сообщение.
                    if (characteristics.Status == GattCommunicationStatus.AccessDenied)
                    {
                        accessDenied = true;
                        lastAvailable = $"служба {service.Uuid}: отказ в доступе";
                        continue;
                    }

                    if (characteristics.Status != GattCommunicationStatus.Success) continue;

                    foreach (var candidate in characteristics.Characteristics)
                    {
                        available.Add(candidate.Uuid.ToString());

                        if (candidate.Uuid == WriteCharacteristic && found == null)
                            found = candidate;

                        if (candidate.Uuid == DeviceNameCharacteristic)
                            nameCharacteristic = candidate;
                    }
                }

                if (found != null)
                {
                    _characteristic = found;
                    return;
                }

                if (accessDenied)
                {
                    // Отказ в доступе к характеристикам службы. Причина почти
                    // всегда одна: устройство уже занято другим приложением.
                    // У недорогих лент соединение только одно, и если с ней
                    // работает приложение телефона (LotusLamp, NBBUFF и
                    // подобные), компьютер получает отказ, хотя устройство
                    // видно и даже числится подключённым.
                    //
                    // Сообщение поэтому называет самую вероятную причину, а не
                    // только «залипший кэш»: второе встречается реже.
                    lastError = new InvalidOperationException(
                        "Windows отказал в доступе к службе ленты. Обычно это значит, " +
                        "что лента уже подключена к телефону: закройте приложение " +
                        "ленты на телефоне или выключите на нём Bluetooth и повторите. " +
                        "Реже помогает выключение и включение Bluetooth на компьютере.");

                    await Task.Delay(800);
                    continue;
                }

                lastAvailable = string.Join("; ", available);

                if (found != null)
                {
                    _characteristic = found;
                    return;
                }

                // Видна только стандартная служба с именем, а
                // производительной службы FFF0 нет. Это значит, что Windows
                // ещё не установила настоящую связь и отдаёт только
                // закэшированную часть таблицы. Чтение имени заставляет
                // связь подняться, после чего FFF0 появляется. Без этого
                // шага повторные попытки ничего не меняли.
                if (nameCharacteristic != null)
                {
                    try
                    {
                        await nameCharacteristic.ReadValueAsync(BluetoothCacheMode.Uncached);
                    }
                    catch (Exception)
                    {
                        // Чтение может не пройти -- это лишь попытка поднять связь.
                    }
                }

                lastError = new InvalidOperationException(
                    "Служба FFF0 ещё не видна");

                await Task.Delay(400);
            }
            catch (Exception ex)
            {
                lastError = ex;
                await Task.Delay(500);
            }
        }

        // Последняя попытка поднять связь: пересоздаём устройство и читаем
        // имя. Чтение имени заставляет Windows установить настоящую связь,
        // после чего производительная служба обычно открывается. Это
        // помогает, когда первые попытки упирались в устаревшее состояние
        // соединения.
        try
        {
            await DisposeDeviceAsync(1200);

            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
            if (_device != null)
            {
                var services = await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached);

                foreach (var service in services.Services)
                {
                    var characteristics =
                        await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);

                    if (characteristics.Status != GattCommunicationStatus.Success) continue;

                    foreach (var candidate in characteristics.Characteristics)
                    {
                        if (candidate.Uuid == WriteCharacteristic)
                        {
                            _characteristic = candidate;
                            return;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            lastError = ex;
        }

        throw new InvalidOperationException(
            $"Не удалось получить характеристику записи FFF3 за {maxAttempts} попыток. " +
            $"Последняя ошибка: {lastError?.Message}");
    }

    private async Task DisposeDeviceAsync(int delayMs = 300)
    {
        if (_device == null)
        {
            if (delayMs > 0) await Task.Delay(delayMs);
            return;
        }

        try { _device.Dispose(); } catch (Exception) { }
        _device = null;

        // Пауза перед новым подключением: сразу после освобождения
        // Windows ещё держит связь, и повторный запрос возвращает тот же
        // неполный список служб.
        if (delayMs > 0) await Task.Delay(delayMs);
    }

    /// <summary>Состояние связи для диагностики.</summary>
    private string _connectionState = "неизвестно";

    public string ConnectionState => _connectionState;

    public void Disconnect()
    {
        _stopRequested = true;
        _wake.Set();

        if (_worker is { IsAlive: true })
        {
            _worker.Join(TimeSpan.FromSeconds(1));
            _worker = null;
        }

        try { _characteristic = null; } catch (Exception) { }
        try { _device?.Dispose(); } catch (Exception) { }
        _device = null;
        _connected = false;
    }

    public void SetColor(Rgb color)
    {
        lock (_stateSync) { _pendingColor = color; }
        _wake.Set();
    }

    public void TurnOn()
    {
        lock (_stateSync) { _pendingOn = true; }
        _wake.Set();
    }

    public void TurnOff()
    {
        lock (_stateSync) { _pendingOff = true; }
        _wake.Set();
    }

    public void SavePreviousState() { }
    public void RestorePreviousState() { }

    private void WorkerLoop()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long lastSend = 0;

        while (!_stopRequested)
        {
            _wake.WaitOne(50);

            // BLE-запись заметно дороже HTTP: 150 мс -- разумный минимум,
            // при котором лента не захлёбывается.
            if (sw.ElapsedMilliseconds - lastSend < 150) continue;

            Rgb? color;
            bool on, off;
            lock (_stateSync)
            {
                color = _pendingColor; _pendingColor = null;
                on = _pendingOn; _pendingOn = false;
                off = _pendingOff; _pendingOff = false;
            }

            if (color == null && !on && !off) continue;
            lastSend = sw.ElapsedMilliseconds;

            try
            {
                if (on) Write(PowerPacket(true));
                else if (off) Write(PowerPacket(false));
                else if (color is { } c) Write(ColorPacket(c));

                _connected = true;
            }
            catch (Exception)
            {
                _connected = false;
            }
        }
    }

    private void Write(byte[] data)
    {
        if (_characteristic == null) throw new InvalidOperationException("Нет соединения");

        // Запись повторяется: устройство уходит в Disconnected, когда с ним
        // никто не работает, и первый обмен после простоя часто не
        // проходит. Повтор поднимает связь.
        Exception? lastError = null;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                GattWriteResult result = WriteAsync(data).GetAwaiter().GetResult();

                if (result.Status == GattCommunicationStatus.Success) return;

                lastError = new InvalidOperationException($"Ошибка записи: {result.Status}");
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            Thread.Sleep(300);
        }

        throw lastError ?? new InvalidOperationException("Ошибка записи");
    }

    private async Task<GattWriteResult> WriteAsync(byte[] data)
    {
        var writer = new Windows.Storage.Streams.DataWriter();
        writer.WriteBytes(data);
        return await _characteristic!.WriteValueWithResultAsync(
            writer.DetachBuffer(), GattWriteOption.WriteWithoutResponse);
    }

    /// <summary>Команда включения/выключения (7e 07 04 .. ef).</summary>
    private static byte[] PowerPacket(bool on) => on
        ? new byte[] { 0x7e, 0x07, 0x04, 0xff, 0x00, 0x01, 0x02, 0x01, 0xef }
        : new byte[] { 0x7e, 0x07, 0x04, 0x00, 0x00, 0x00, 0x02, 0x01, 0xef };

    /// <summary>Команда цвета (7e 07 05 03 R G B 10 ef).</summary>
    private static byte[] ColorPacket(Rgb color)
    {
        var (r, g, b) = color.ToBytes();
        return new byte[] { 0x7e, 0x07, 0x05, 0x03, r, g, b, 0x10, 0xef };
    }

    private static ulong ParseMac(string mac)
    {
        string hex = mac.Replace(":", "").Replace("-", "").Trim();
        if (hex.Length != 12)
            throw new InvalidOperationException($"Некорректный MAC-адрес: {mac}");
        return ulong.Parse(hex, System.Globalization.NumberStyles.HexNumber);
    }

    /// <summary>
    /// Поиск ближайших BLE-устройств, объявляющих своё имя. Фильтра по
    /// бренду нет намеренно: клоны объявляются под разными именами.
    /// </summary>
    public static List<DiscoveredDevice> Scan(TimeSpan timeout, CancellationToken ct = default)
    {
        var found = new Dictionary<ulong, DiscoveredDevice>();
        var sync = new object();

        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active,
        };

        watcher.Received += (_, args) =>
        {
            string name = args.Advertisement.LocalName;
            if (string.IsNullOrWhiteSpace(name)) return;   // безымянные пропускаем

            lock (sync)
            {
                found[args.BluetoothAddress] = new DiscoveredDevice
                {
                    Id = args.BluetoothAddress.ToString("X12"),
                    Address = FormatMac(args.BluetoothAddress),
                    Name = name,
                    IsBluetooth = true,
                };
            }
        };

        watcher.Start();

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            Thread.Sleep(100);

        watcher.Stop();

        lock (sync)
        {
            return found.Values.ToList();
        }
    }

    private static string FormatMac(ulong address)
    {
        string hex = address.ToString("X12");
        return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }

    public void Dispose()
    {
        Disconnect();
        _wake.Dispose();
    }
}
