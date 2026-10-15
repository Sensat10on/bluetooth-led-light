using OpenTuyaSync.Core;

namespace OpenTuyaSync.Capture;

/// <summary>
/// Стробоскоп: мигает с заданной частотой. Реле физически не щёлкает --
/// яркость чередуется между максимумом и нулём внутри одного цвета, чтобы
/// не изнашивать железо частыми включениями.
///
/// Предупреждение: быстро мигающий свет неприятен людям с
/// фоточувствительностью.
/// </summary>
public sealed class FlashCaptureSource : ICaptureSource
{
    private readonly FlashSettings _settings;
    private readonly object _lock = new();
    private readonly Random _random = new();

    private Thread? _thread;
    private volatile bool _stopRequested;
    private Rgb? _color;

    public FlashCaptureSource(FlashSettings settings) => _settings = settings;

    public SourceKind Kind => SourceKind.Flash;

    public string Caption => "Режим вспышек (стробоскоп)";

    public bool IsRunning { get; private set; }

    public string? LastError => null;

    public void Start()
    {
        if (IsRunning) return;

        _stopRequested = false;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "flash-source",
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
        IsRunning = true;
    }

    public void Stop()
    {
        _stopRequested = true;

        if (_thread is { IsAlive: true })
        {
            _thread.Join(TimeSpan.FromSeconds(1));
            _thread = null;
        }

        IsRunning = false;
    }

    public Rgb? TryGetColor()
    {
        lock (_lock) return _color;
    }

    private void Run()
    {
        double hz = Math.Clamp(_settings.Hz, 0.1, 20.0);
        int halfPeriodMs = Math.Max(10, (int)(1000.0 / hz / 2.0));
        bool on = false;

        while (!_stopRequested)
        {
            on = !on;

            Rgb color;
            if (on)
            {
                float hue = _settings.Colorful ? (float)(_random.NextDouble() * 360.0) : 0f;
                float sat = _settings.Colorful ? 1f : 0f;
                color = Rgb.FromHsv(hue, sat, 1f);
            }
            else
            {
                color = Rgb.Black;
            }

            lock (_lock) _color = color;
            Thread.Sleep(halfPeriodMs);
        }
    }

    public void Dispose() => Stop();
}
