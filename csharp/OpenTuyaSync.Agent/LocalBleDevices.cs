using OpenTuyaSync.Core;
using OpenTuyaSync.Providers;

namespace OpenTuyaSync.Agent;

/// <summary>
/// Управляет Bluetooth-лентами из приложения, а не из службы.
///
/// Причина разделения: служба Windows работает в изолированной сессии 0, и
/// устройства Bluetooth Low Energy ей недоступны -- операции GATT требуют
/// сессии пользователя. Проверено на практике: лента находится поиском из
/// приложения (сессия 1) и не находится из службы (сессия 0), причём
/// Windows сообщает именно «устройство не найдено».
///
/// Поэтому Bluetooth-устройства обслуживает приложение: цвет уходит в ленту
/// напрямую, минуя службу. Для пользователя разницы нет -- лента получает
/// тот же цвет в тот же момент.
/// </summary>
public sealed class LocalBleDevices : IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<string, IColorProvider> _providers = new();
    private readonly List<IColorProvider> _retiring = new();

    private int _generation;
    private bool _disposed;

    /// <summary>
    /// Номер текущего намерения. Увеличивается при каждом подключении и
    /// отключении. Фоновые операции сверяют его с текущим: если номер
    /// изменился, значит пользователь уже переключил источник и результат
    /// устаревшей операции применять нельзя.
    ///
    /// Без этого при быстрой смене источника подключение к ленте
    /// завершалось уже после остановки и оставляло устройство занятым --
    /// поэтому лента переставала подключаться.
    /// </summary>
    public int Generation => Volatile.Read(ref _generation);

    /// <summary>Номер операции, выполняемой прямо сейчас.</summary>
    public int PendingGeneration => Volatile.Read(ref _pending);

    private int _pending;

    /// <summary>Сколько Bluetooth-устройств подключено.</summary>
    public int ConnectedCount
    {
        get
        {
            lock (_sync) return _providers.Values.Count(p => p.IsConnected);
        }
    }

    /// <summary>
    /// Идёт ли подключение прямо сейчас.
    ///
    /// Важно сбрасывать признак в конце: без этого строка состояния
    /// навсегда осталась бы с надписью «подключение...».
    /// </summary>
    public bool IsBusy => _connecting;

    /// <summary>
    /// Сколько неудачных подключений подряд делается, прежде чем на время
    /// прекратить попытки.
    ///
    /// Зачем пауза. Когда лента занята телефоном, каждая попытка подключения
    /// поднимает трафик в эфире: поиск устройства, установление связи,
    /// запрос списка служб и характеристик. Если повторять это без конца,
    /// радио компьютера работает непрерывно, и рядом стоящие Bluetooth-
    /// устройства теряют связь -- так пропадал пульт приставки Xiaomi,
    /// которая стоит в метре от компьютера. Пауза нужна, чтобы эфир
    /// освобождался.
    /// </summary>
    private const int FailuresBeforePause = 3;

    /// <summary>Сколько ждать после серии неудач.</summary>
    private static readonly TimeSpan FailurePause = TimeSpan.FromMinutes(2);

    private int _consecutiveFailures;
    private DateTime _pauseUntil = DateTime.MinValue;

    /// <summary>
    /// Идёт ли сейчас пауза после серии неудач.
    /// </summary>
    public bool IsPaused => DateTime.UtcNow < _pauseUntil;

    /// <summary>
    /// Сколько секунд осталось до конца паузы.
    /// </summary>
    public int PauseSecondsLeft => IsPaused
        ? (int)Math.Ceiling((_pauseUntil - DateTime.UtcNow).TotalSeconds)
        : 0;

    /// <summary>
    /// Разрешает попытки немедленно -- для кнопки «Переподключить».
    ///
    /// Пользователь мог устранить причину (закрыть приложение ленты на
    /// телефоне) и не должен ждать конца паузы.
    /// </summary>
    public void ResetFailurePause()
    {
        _pauseUntil = DateTime.MinValue;
        _consecutiveFailures = 0;
        ConnectFailed?.Invoke("Попытки подключения возобновлены вручную.");
    }

    private volatile bool _connecting;

    /// <summary>
    /// Номер, который получит следующее подключение. Вызывающий код
    /// запоминает его до запуска подключения в фоне и потом сверяет: если
    /// номер изменился, результат устарел и применять его нельзя.
    /// </summary>
    public int ConnectAsyncGeneration => Generation + 1;

    /// <summary>
    /// Одновременно выполняется только одно подключение.
    ///
    /// Зачем. Подключение запускается из двух мест: кнопкой «Запустить» и
    /// ручным цветом, если лента ещё не подключена. При быстром переключении
    /// «ручной → автоматический» оба запуска шли одновременно: каждый снимал
    /// устройства предыдущего запуска, и лента в итоге не подключалась ни к
    /// одному из них. Внешне это выглядело как «не подключается».
    /// </summary>
    private readonly SemaphoreSlim _connectGate = new(1, 1);

    /// <summary>
    /// Подключается ко всем включённым Bluetooth-устройствам из настроек.
    /// Ошибка одного устройства не мешает остальным.
    /// </summary>
    public List<string> Connect(AppConfig config)
    {
        var errors = new List<string>();

        // Ждём, пока завершится уже идущее подключение. Ограничение по
        // времени обязательно: подключение к BLE-устройству занимает секунды,
        // но бесконечно ждать нельзя.
        if (!_connectGate.Wait(TimeSpan.FromSeconds(45)))
        {
            errors.Add("Подключение уже выполняется: повторный запрос пропущен.");
            return errors;
        }

        try
        {
            return ConnectCore(config);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private List<string> ConnectCore(AppConfig config)
    {
        var errors = new List<string>();
        int generation = Interlocked.Increment(ref _generation);

        // Пауза после серии неудач: эфир должен освободиться.
        //
        // Проверка идёт до всего остального, потому что смысл паузы --
        // не поднимать трафик вообще. Без неё программа долбила подключение
        // по кругу, пока лента занята телефоном, и радио компьютера работало
        // непрерывно: от этого терял связь пульт приставки, стоящей рядом.
        if (IsPaused)
        {
            errors.Add(
                $"{config.Devices.Count(d => d.Enabled && d.Type == DeviceKind.ElkBledob)} " +
                $"устройств(о) не подключено: пауза после {_consecutiveFailures} неудачных попыток, " +
                $"осталось {PauseSecondsLeft} с. Причина обычно в том, что лента занята телефоном.");

            return errors;
        }

        Volatile.Write(ref _pending, generation);
        _connecting = true;

        try
        {
            // Прежние устройства снимаем в фоне: их рабочие потоки могут
            // ждать завершения, и в потоке интерфейса это недопустимо.
            RetireCurrent();

            foreach (var device in config.Devices.Where(d => d.Enabled && d.Type == DeviceKind.ElkBledob))
            {
                // Пользователь успел переключить источник -- прекращаем.
                if (Generation != generation) break;

                try
                {
                    var provider = new ElkBledobProvider(device);

                    try
                    {
                        provider.Connect();
                    }
                    catch (Exception ex) when (LooksLikeStickyCache(ex))
                    {
                        // Windows отказал в доступе к службе устройства --
                        // признак залипшего кэша GATT. Помогает перезапуск
                        // Bluetooth, но он разрывает ВСЕ Bluetooth-соединения
                        // компьютера: мышь, наушники и пульты других устройств.
                        // Поэтому он выполняется только с разрешения в
                        // настройках и не чаще раза в десять минут.
                        if (!config.AllowBluetoothRecovery)
                        {
                            errors.Add(
                                $"{device.Name}: {ex.Message} Перезапуск Bluetooth выключен " +
                                "в настройках: он разрывает связь с другими Bluetooth-устройствами. " +
                                "Освободите ленту (закройте приложение ленты на телефоне) и повторите.");

                            try { provider.Dispose(); } catch (Exception) { }
                            continue;
                        }

                        if (!ElkBledobProvider.CanRestartRadio())
                        {
                            errors.Add(
                                $"{device.Name}: {ex.Message} Перезапуск Bluetooth недавно уже " +
                                "выполнялся, повторю позже: частые перезапуски рвут связь с другими " +
                                "Bluetooth-устройствами.");

                            try { provider.Dispose(); } catch (Exception) { }
                            continue;
                        }

                        errors.Add($"{device.Name}: {ex.Message} Перезапускаю Bluetooth и пробую снова...");

                        try { provider.Dispose(); } catch (Exception) { }

                        var retry = new ElkBledobProvider(device);

                        // Если перезапуск не состоялся (не хватило прав или
                        // сработало ограничение частоты), оставляем попытку
                        // подключения прежнему провайдеру: он ещё рабочий.
                        if (!retry.ConnectWithRadioRestart())
                        {
                            errors.Add(
                                $"{device.Name}: перезапуск Bluetooth не выполнен, " +
                                "пробую подключиться без него.");

                            try { retry.Dispose(); } catch (Exception) { }

                            try { provider.Connect(); }
                            catch (Exception retryError)
                            {
                                errors.Add($"{device.Name}: {retryError.Message}");
                                try { provider.Dispose(); } catch (Exception) { }
                                continue;
                            }

                            if (Generation != generation)
                            {
                                try { provider.Dispose(); } catch (Exception) { }
                                break;
                            }

                            lock (_sync) _providers[device.Id] = provider;
                            continue;
                        }

                        if (Generation != generation)
                        {
                            try { retry.Dispose(); } catch (Exception) { }
                            break;
                        }

                        lock (_sync) _providers[device.Id] = retry;
                        errors.RemoveAll(e => e.StartsWith(device.Name, StringComparison.Ordinal));
                        continue;
                    }

                    if (Generation != generation)
                    {
                        // Подключение завершилось, когда оно уже не нужно.
                        try { provider.Dispose(); } catch (Exception) { }
                        break;
                    }

                    lock (_sync) _providers[device.Id] = provider;
                }
                catch (Exception ex)
                {
                    errors.Add($"{device.Name}: {ex.Message}");
                }
            }
        }
        finally
        {
            if (Volatile.Read(ref _pending) == generation)
                Volatile.Write(ref _pending, 0);

            _connecting = false;

            // Считаем неудачи, чтобы после серии прекратить попытки на время
            // и дать эфиру освободиться: непрерывные попытки подключения
            // мешают другим Bluetooth-устройствам рядом.
            bool anyConnected;
            lock (_sync) anyConnected = _providers.Count > 0;

            if (anyConnected)
            {
                _consecutiveFailures = 0;
                _pauseUntil = DateTime.MinValue;
            }
            else
            {
                _consecutiveFailures++;

                if (_consecutiveFailures >= FailuresBeforePause)
                {
                    _pauseUntil = DateTime.UtcNow + FailurePause;

                    errors.Add(
                        $"Подключение не удалось {_consecutiveFailures} раз подряд. " +
                        $"Попытки приостановлены на {FailurePause.TotalMinutes:0} мин, чтобы " +
                        "не занимать эфир: непрерывные попытки мешают другим " +
                        "Bluetooth-устройствам рядом. Нажмите «Переподключить устройства», " +
                        "чтобы попробовать сразу.");

                    _consecutiveFailures = 0;
                }
            }
        }

        return errors;
    }

    /// <summary>
    /// Снимает текущие устройства. Работа с Bluetooth выполняется в фоне:
    /// рабочие потоки провайдеров могут ждать завершения, и делать это в
    /// потоке интерфейса нельзя -- окно замёрзнет.
    /// </summary>
    private void RetireCurrent()
    {
        lock (_sync)
        {
            _retiring.AddRange(_providers.Values);
            _providers.Clear();
        }
    }

    /// <summary>
    /// Разбирается с отложенными устройствами. Вызывается после остановки
    /// подсветки, когда рабочие потоки уже завершились.
    /// </summary>
    private void ReapRetired()
    {
        List<IColorProvider> retired;
        lock (_sync)
        {
            retired = new List<IColorProvider>(_retiring);
            _retiring.Clear();
        }

        foreach (var provider in retired)
        {
            try { provider.Dispose(); } catch (Exception) { }
        }
    }

    /// <summary>
    /// Пересылает цвет всем подключённым лентам.
    ///
    /// Вызывается из потока движка захвата (несколько раз в секунду), и
    /// одновременно с ним цвет может отправить ручное управление из потока
    /// интерфейса. Поэтому список устройств берётся и используется под
    /// одной блокировкой: иначе устройство могло быть освобождено прямо во
    /// время отправки в него цвета, и подсветка вставала.
    ///
    /// Проверка IsConnected здесь намеренно НЕ делается: ручную команду
    /// терять нельзя. Лента, ушедшая в сон, отвечает на первое обращение не
    /// сразу, и отброшенная команда означала бы, что нажатие в окне ничего
    /// не делает. Провайдер сам поднимает связь и повторяет запись.
    /// </summary>
    public void ApplyColor(Rgb color)
    {
        lock (_sync)
        {
            foreach (var provider in _providers.Values)
            {
                try { provider.SetColor(color); }
                catch (Exception) { }
            }
        }
    }

    /// <summary>
    /// Сообщение о неудаче подключения для интерфейса.
    ///
    /// Нужно потому, что при ручной команде подключение идёт в фоне, и без
    /// этого сообщения пользователь видит только «Bluetooth: подключение...»
    /// без конца: непонятно ни что случилось, ни что делать. Именно так и
    /// произошло, когда лента была занята телефоном.
    /// </summary>
    public event Action<string>? ConnectFailed;

    /// <summary>
    /// Отправляет цвет и, если ленты в списке нет, сперва подключает её.
    ///
    /// Нужно для ручного управления. Подключение запускается в фоне и
    /// занимает секунды, поэтому команда ставится в очередь сразу, а
    /// вызывающий код не ждёт. Без этого нажатие «Применить цвет» в окне,
    /// которое открыли сразу после остановки подсветки, уходило в пустоту:
    /// список устройств в этот момент ещё пуст.
    ///
    /// Возвращает true, если лента подключена и цвет поставлен в очередь
    /// сразу; false -- если подключение только начато.
    /// </summary>
    public bool ApplyColorOrConnect(AppConfig config, Rgb color)
    {
        bool connected;

        lock (_sync)
        {
            connected = _providers.Count > 0;

            foreach (var provider in _providers.Values)
            {
                try { provider.SetColor(color); }
                catch (Exception) { }
            }
        }

        if (connected) return true;

        // Ленты нет -- подключаем в фоне и повторяем команду, когда связь
        // появится. Признак _connecting защищает от потока одинаковых
        // попыток: ручной цвет отправляется многократно.
        if (!_connecting) Task.Run(() => ConnectAndSend(config, color));

        return false;
    }

    private void ConnectAndSend(AppConfig config, Rgb color)
    {
        try
        {
            var errors = Connect(config);

            if (_providers.Count > 0)
            {
                ApplyColor(color);
                return;
            }

            ReportConnectFailure(errors);
        }
        catch (Exception ex)
        {
            ReportConnectFailure(new List<string> { ex.Message });
        }
    }

    /// <summary>
    /// Сообщает интерфейсу, что подключиться не удалось.
    ///
    /// Ошибки от устройств перечисляются как есть: в них написана причина --
    /// чаще всего «лента занята телефоном».
    /// </summary>
    private void ReportConnectFailure(List<string> errors)
    {
        string message = errors.Count > 0
            ? string.Join("; ", errors)
            : "Не удалось подключиться: устройство не отвечает";

        try { ConnectFailed?.Invoke(message); }
        catch (Exception) { }
    }

    public void TurnOn()
    {
        lock (_sync)
        {
            foreach (var provider in _providers.Values)
            {
                try { provider.TurnOn(); } catch (Exception) { }
            }
        }
    }

    /// <summary>
    /// Включает ленту, подключая её при необходимости. Устроено так же, как
    /// ApplyColorOrConnect: кнопка «Включить» должна работать и тогда, когда
    /// лента ещё не в списке.
    /// </summary>
    public bool TurnOnOrConnect(AppConfig config)
    {
        bool connected;

        lock (_sync)
        {
            connected = _providers.Count > 0;
            foreach (var provider in _providers.Values)
            {
                try { provider.TurnOn(); } catch (Exception) { }
            }
        }

        if (connected) return true;

        if (!_connecting) Task.Run(() => ConnectAndPower(config, on: true));
        return false;
    }

    /// <summary>Выключает ленту и подключает её при необходимости.</summary>
    public bool TurnOffOrConnect(AppConfig config)
    {
        bool connected;

        lock (_sync)
        {
            connected = _providers.Count > 0;
            foreach (var provider in _providers.Values)
            {
                try { provider.TurnOff(); } catch (Exception) { }
            }
        }

        if (connected) return true;

        if (!_connecting) Task.Run(() => ConnectAndPower(config, on: false));
        return false;
    }

    private void ConnectAndPower(AppConfig config, bool on)
    {
        try
        {
            var errors = Connect(config);

            if (_providers.Count > 0)
            {
                lock (_sync)
                {
                    foreach (var provider in _providers.Values)
                    {
                        try
                        {
                            if (on) provider.TurnOn();
                            else provider.TurnOff();
                        }
                        catch (Exception) { }
                    }
                }

                return;
            }

            ReportConnectFailure(errors);
        }
        catch (Exception ex)
        {
            ReportConnectFailure(new List<string> { ex.Message });
        }
    }

    public void TurnOff()
    {
        lock (_sync)
        {
            foreach (var provider in _providers.Values)
            {
                try { provider.TurnOff(); } catch (Exception) { }
            }
        }
    }

    /// <summary>Подключается заново к конкретному устройству. Нужно после
    /// изменения настроек или проверки связи.</summary>
    /// <summary>
    /// Подключается заново к конкретному устройству.
    ///
    /// Устройство остаётся рабочим, даже если новая связь не установилась:
    /// прежнее подключение возвращается на место. Без этого неудачное
    /// переподключение оставляло систему вообще без ленты -- со стороны это
    /// выглядело так, будто подсветка пропала.
    ///
    /// Выполняется долго (до тридцати секунд), поэтому вызывать только из
    /// фона.
    /// </summary>
    public string? Reconnect(AppConfig config, string deviceId)
    {
        var device = config.Devices.FirstOrDefault(d => d.Id == deviceId);
        if (device == null || device.Type != DeviceKind.ElkBledob) return null;

        IColorProvider? previous;
        lock (_sync)
        {
            _providers.Remove(deviceId, out previous);
        }

        try
        {
            var provider = new ElkBledobProvider(device);
            provider.Connect();

            lock (_sync) _providers[deviceId] = provider;

            // Новое подключение встало -- прежнее больше не нужно.
            try { previous?.Dispose(); } catch (Exception) { }
            return null;
        }
        catch (Exception ex)
        {
            // Не получилось: возвращаем прежнее подключение на место, чтобы
            // лента продолжала работать.
            if (previous != null)
            {
                lock (_sync) _providers[deviceId] = previous;
                return $"{device.Name}: {ex.Message} (прежнее подключение сохранено)";
            }

            return $"{device.Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// Быстро проверяет подключение: самопроверка без ожидания.
    /// Возвращает null при успехе или текст ошибки.
    /// </summary>
    public string? Probe(AppConfig config)
    {
        var device = config.Devices.FirstOrDefault(d => d.Enabled && d.Type == DeviceKind.ElkBledob);
        if (device == null) return "В настройках нет Bluetooth-устройств";

        return Reconnect(config, device.Id);
    }

    /// <summary>
    /// Признак залипшего кэша GATT: Windows отказывает в доступе к
    /// производительной службе устройства. Проверяем по тексту, потому что
    /// разные слои сообщают об этом по-разному.
    /// </summary>
    private static bool LooksLikeStickyCache(Exception ex)
    {
        string message = ex.Message;

        return message.Contains("отказ в доступе", StringComparison.OrdinalIgnoreCase)
               || message.Contains("AccessDenied", StringComparison.OrdinalIgnoreCase)
               || message.Contains("залипший кэш", StringComparison.OrdinalIgnoreCase);
    }

    public string StatusText
    {
        get
        {
            lock (_sync)
            {
                if (_providers.Count == 0) return "Bluetooth-устройств нет";
                int connected = _providers.Values.Count(p => p.IsConnected);
                return $"Bluetooth: подключено {connected} из {_providers.Count}";
            }
        }
    }

    /// <summary>
    /// Отпускает все ленты и помечает прежние операции устаревшими.
    ///
    /// Вызов быстрый: он только снимает ссылки и увеличивает номер
    /// намерения. Фактическая работа с Bluetooth идёт в фоне -- если делать
    /// её здесь, поток интерфейса замёрзнет, потому что рабочие потоки
    /// провайдеров ждут завершения.
    /// </summary>
    public void DisconnectAll()
    {
        Interlocked.Increment(ref _generation);
        Volatile.Write(ref _pending, 0);

        lock (_sync)
        {
            _retiring.AddRange(_providers.Values);
            _providers.Clear();
        }

        // Разбираемся с уже снятыми устройствами, не дожидаясь остальных.
        Task.Run(ReapRetired);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        DisconnectAll();
        ReapRetired();
    }
}
