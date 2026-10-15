using System.Net.Http;
using System.Text;
using OpenTuyaSync.Core;

namespace OpenTuyaSync.Providers;

/// <summary>
/// Общая основа для устройств, которые общаются по HTTP (WLED, Hue).
///
/// Даёт то, что нужно всем сетевым выходам: один долгоживущий HttpClient
/// (создавать его на каждый запрос -- классическая утечка сокетов),
/// отдельный поток отправки с ограничением частоты и переподключение при
/// разовой ошибке. Вызывающий код возвращается сразу же.
/// </summary>
public abstract class HttpProviderBase : IColorProvider
{
    private static readonly HttpClient Shared = new(new SocketsHttpHandler
    {
        // Управление светом -- короткие частые запросы. Отключаем пул
        // соединений на минуты и не даём висеть мёртвым сокетам.
        PooledConnectionLifetime = TimeSpan.FromSeconds(30),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15),
        ConnectTimeout = TimeSpan.FromSeconds(2),
        MaxConnectionsPerServer = 2,
    })
    {
        Timeout = TimeSpan.FromSeconds(2),
    };

    protected static HttpClient Http => Shared;

    private readonly AutoResetEvent _wake = new(false);
    private readonly object _stateSync = new();

    private Thread? _worker;
    private volatile bool _stopRequested;
    private volatile bool _connected;

    private Rgb? _pendingColor;
    private bool _pendingOn, _pendingOff;

    /// <summary>Минимальный интервал между отправками, мс.</summary>
    protected abstract int MinSendIntervalMs { get; }

    protected string DeviceId { get; }

    protected HttpProviderBase(string deviceId) => DeviceId = deviceId;

    public abstract DeviceKind Kind { get; }
    public abstract string Caption { get; }
    public abstract string StatusText { get; }

    public bool IsConnected => _connected;

    /// <summary>Проверка доступности устройства при подключении.</summary>
    protected abstract void ProbeConnection();

    /// <summary>Отправка цвета конкретному устройству.</summary>
    protected abstract void SendColorCore(Rgb color);

    protected abstract void TurnOnCore();
    protected abstract void TurnOffCore();

    public virtual void Connect()
    {
        _stopRequested = false;
        ProbeConnection();
        _connected = true;

        if (_worker == null)
        {
            _worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"http-sender-{DeviceId}",
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

    public virtual void SavePreviousState() { }
    public virtual void RestorePreviousState() { }

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
                color = _pendingColor; _pendingColor = null;
                on = _pendingOn; _pendingOn = false;
                off = _pendingOff; _pendingOff = false;
            }

            if (color == null && !on && !off) continue;
            lastSend = now;

            try
            {
                if (on) TurnOnCore();
                else if (off) TurnOffCore();
                else if (color is { } c) SendColorCore(c);

                _connected = true;
            }
            catch (Exception)
            {
                // Устройство недоступно: помечаем как отключённое, но не
                // выбрасываем поток -- следующая попытка будет через
                // MinSendIntervalMs.
                _connected = false;
            }
        }
    }

    public virtual void Dispose()
    {
        Disconnect();
        _wake.Dispose();
    }
}
