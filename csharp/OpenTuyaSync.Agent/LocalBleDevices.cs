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

    private volatile bool _connecting;

    /// <summary>
    /// Номер, который получит следующее подключение. Вызывающий код
    /// запоминает его до запуска подключения в фоне и потом сверяет: если
    /// номер изменился, результат устарел и применять его нельзя.
    /// </summary>
    public int ConnectAsyncGeneration => Generation + 1;

    /// <summary>
    /// Подключается ко всем включённым Bluetooth-устройствам из настроек.
    /// Ошибка одного устройства не мешает остальным.
    /// </summary>
    public List<string> Connect(AppConfig config)
    {
        var errors = new List<string>();
        int generation = Interlocked.Increment(ref _generation);
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
                        // признак залипшего кэша GATT. Перезапускаем
                        // Bluetooth и пробуем ещё раз: вручную это делается
                        // через параметры Windows, но приложение может
                        // сделать это само.
                        errors.Add($"{device.Name}: {ex.Message} Перезапускаю Bluetooth и пробую снова...");

                        try { provider.Dispose(); } catch (Exception) { }

                        var retry = new ElkBledobProvider(device);
                        retry.ConnectWithRadioRestart();

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
            Connect(config);
            ApplyColor(color);
        }
        catch (Exception)
        {
            // Ошибку подключения уже записал Connect через список ошибок.
        }
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
            Connect(config);

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
        }
        catch (Exception)
        {
            // Ошибку подключения уже записал Connect.
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
