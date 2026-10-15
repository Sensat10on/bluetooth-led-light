using OpenTuyaSync.Core;

namespace OpenTuyaSync.Providers.Tuya;

/// <summary>
/// Выход на лампы Tuya и их белые клоны (LSC Smart Connect, Nedis
/// SmartLife, Mirabella Genio, Treatlife, Ledvance SMART+ WiFi).
///
/// Отправка идёт через отдельный рабочий поток с ограничением частоты.
/// Это принципиально: TCP-обмен с лампой занимает десятки миллисекунд и
/// иногда подвисает при переподключении, а вызывающий код (движок
/// подсветки) обязан возвращаться мгновенно, иначе проседают кадры.
/// </summary>
public sealed class TuyaProvider : IColorProvider
{
    /// <summary>Ниже этой насыщенности цвет отправляется как белый (dp 22),
    /// а не как цветной (dp 24). У большинства ламп Tuya белый светодиод
    /// заметно ярче, чем RGB, поэтому официальное приложение делает так же.</summary>
    private const float WhiteSaturationThreshold = 0.03f;

    /// <summary>Минимальный интервал между отправками на устройство.</summary>
    private const int MinSendIntervalMs = 90;

    private readonly DeviceConfig _config;
    private readonly TuyaClient _client;

    private readonly AutoResetEvent _wake = new(false);
    private readonly object _stateSync = new();

    private Thread? _worker;
    private volatile bool _stopRequested;

    private Rgb? _pendingColor;
    private bool _pendingOn, _pendingOff;
    private volatile bool _connected;

    private byte[]? _sceneData;
    private int _brightness = 1000;

    public TuyaProvider(DeviceConfig config)
    {
        _config = config;
        var t = config.Tuya ?? throw new ArgumentException("Не заданы параметры Tuya");
        _client = new TuyaClient(t.DevId, t.Ip, t.LocalKey, t.Version);
    }

    public DeviceKind Kind => DeviceKind.Tuya;
    public string Caption => "Tuya / Smart Life";
    public bool IsConnected => _connected;

    public string StatusText => _connected
        ? $"подключено, {_config.Tuya?.Ip}"
        : "не подключено";

    public void Connect()
    {
        _stopRequested = false;
        _client.Connect();
        _connected = true;

        // Прогреваем соединение запросом состояния: он же подтверждает,
        // что Local Key и версия протокола верны.
        try { _client.QueryStatus(); }
        catch (Exception) { /* не критично, отправка попробует снова */ }

        if (_worker == null)
        {
            _worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"tuya-sender-{_config.Id}",
                // Ниже обычного приоритета: сетевой обмен не должен
                // отбирать кванты у захвата экрана.
                Priority = ThreadPriority.BelowNormal,
            };
            _worker.Start();
        }
    }

    public void Disconnect()
    {
        _stopRequested = true;
        _wake.Set();

        if (_worker is { IsAlive: true })
        {
            _worker.Join(TimeSpan.FromSeconds(1));
            _worker = null;
        }

        _client.Dispose();
        _connected = false;
    }

    public void SetColor(Rgb color)
    {
        lock (_stateSync)
        {
            _pendingColor = color;

            // Потолок яркости из настроек интерфейса.
            var (_, _, v) = color.ToHsv();
            _brightness = Math.Max(10, (int)Math.Round(v * 1000));
        }
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

    public void SavePreviousState()
    {
        try
        {
            var status = _client.QueryStatus();
            var dps = status?["dps"] as System.Text.Json.Nodes.JsonObject;
            if (dps?["25"] != null)
                _sceneData = System.Text.Encoding.UTF8.GetBytes(dps["25"]!.ToJsonString());
        }
        catch (Exception)
        {
            _sceneData = null;   // состояние сцены недоступно -- не критично
        }
    }

    public void RestorePreviousState()
    {
        if (_sceneData == null) return;

        try
        {
            string scene = System.Text.Encoding.UTF8.GetString(_sceneData).Trim('"');
            _client.SendDps(new Dictionary<string, object>
            {
                ["21"] = "scene",
                ["25"] = scene,
            });
        }
        catch (Exception) { }
    }

    /// <summary>Рабочий поток: забирает последний запрошенный цвет и
    /// отправляет его, соблюдая минимальный интервал.</summary>
    private void WorkerLoop()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long lastSend = 0;

        while (!_stopRequested)
        {
            _wake.WaitOne(50);

            long now = sw.ElapsedMilliseconds;
            if (now - lastSend < MinSendIntervalMs) continue;

            Rgb? color;
            bool on, off;
            lock (_stateSync)
            {
                color = _pendingColor;
                _pendingColor = null;
                on = _pendingOn; off = _pendingOff;
                _pendingOn = false; _pendingOff = false;
            }

            if (color == null && !on && !off) continue;

            lastSend = now;

            try
            {
                if (on) _client.SendDps(new Dictionary<string, object> { ["20"] = true });
                else if (off) _client.SendDps(new Dictionary<string, object> { ["20"] = false });
                else if (color is { } c) SendColor(c);

                _connected = true;
            }
            catch (Exception)
            {
                // Разовая неудачная отправка не должна ронять подсветку:
                // переподключаемся и продолжим со следующего кадра.
                _connected = false;
                try
                {
                    _client.Connect();
                    _connected = true;
                }
                catch (Exception)
                {
                    Thread.Sleep(500);   // лампа недоступна -- не долбим сеть
                }
            }
        }
    }

    private void SendColor(Rgb color)
    {
        var (_, s, _) = color.ToHsv();

        if (s < WhiteSaturationThreshold)
        {
            _client.SendDps(new Dictionary<string, object>
            {
                ["21"] = "white",
                ["22"] = _brightness,
            });
        }
        else
        {
            _client.SendDps(new Dictionary<string, object>
            {
                ["21"] = "colour",
                ["24"] = ToTuyaHex(color),
            });
        }
    }

    /// <summary>Tuya ждёт цвет строкой hhhhssssvvvv: оттенок 0-360,
    /// насыщенность и яркость по шкале 0-1000.</summary>
    private static string ToTuyaHex(Rgb color)
    {
        var (h, s, v) = color.ToHsv();
        return $"{(int)Math.Round(h) % 360:x4}{(int)Math.Round(s * 1000):x4}{(int)Math.Round(v * 1000):x4}";
    }

    public void Dispose()
    {
        Disconnect();
        _wake.Dispose();
    }
}
