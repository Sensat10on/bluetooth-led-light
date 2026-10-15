using OpenTuyaSync.Core;

namespace OpenTuyaSync.Capture;

/// <summary>
/// Ambilight: цвет с экрана. Работает в отдельном потоке и отдаёт
/// последний посчитанный цвет без блокировки.
///
/// Размер выборки намеренно крошечный (по умолчанию 64x36 пикселей).
/// Одна лампа всё равно не покажет детали, а усреднение по тысячам
/// пикселей каждый кадр -- это чистая трата процессорного времени: на
/// 64x36 кадр обрабатывается за десятки микросекунд, тогда как полный
/// кадр 1920x1080 -- за миллисекунды, которые в игре не лишние.
/// </summary>
public sealed class ScreenCaptureSource : ICaptureSource
{
    private const int TargetWidth = 64;
    private const int TargetHeight = 36;

    private readonly ScreenSettings _settings;
    private readonly object _lock = new();
    private readonly float[] _samples;
    private readonly byte[] _frame;

    private Thread? _thread;
    private volatile bool _stopRequested;
    private Rgb? _color;
    private Rgb? _smoothed;
    private IScreenBackend? _backend;

    /// <summary>
    /// Сколько раз DXGI не смог отдать кадр. После нескольких неудач
    /// повторные попытки прекращаются: если дублирование рабочего стола
    /// недоступно (удалённый рабочий стол, ограниченная сессия), каждая
    /// попытка -- это лишняя работа видеодрайвера.
    /// </summary>
    private int _dxgiFailures;
    private const int DxgiFailureLimit = 3;

    /// <summary>Сколько кадров подряд не пришло от текущего бэкенда.</summary>
    private int _consecutiveMisses;

    public ScreenCaptureSource(ScreenSettings settings)
    {
        _settings = settings;
        _frame = new byte[TargetWidth * TargetHeight * 3];
        _samples = new float[TargetWidth * TargetHeight * 3];
    }

    public SourceKind Kind => SourceKind.Screen;

    public string Caption => "Ambilight (экран)";

    public bool IsRunning { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Имя активного бэкенда: DXGI или GDI. Показывается в
    /// интерфейсе, потому что от него зависит работа с играми.</summary>
    public string BackendName { get; private set; } = "—";

    /// <summary>Диагностика: сколько кадров отдал бэкенд, сколько раз
    /// промолчал и сколько раз бэкенд пересоздавался.</summary>
    public long FramesCaptured { get; private set; }
    public long FramesMissed { get; private set; }
    public int BackendRebuilds { get; private set; }

    /// <summary>Отдавал ли DXGI настоящие кадры хотя бы раз.</summary>
    public bool DxgiProducedAnyFrame { get; private set; }

    public void Start()
    {
        if (IsRunning) return;

        _stopRequested = false;
        LastError = null;
        _smoothed = null;

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "ambilight-capture",
            // Выше обычного: захват экрана задаёт плавность подсветки, и
            // ему важнее получить квант, чем фоновым задачам.
            Priority = ThreadPriority.AboveNormal,
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
        CreateBackend();

        while (!_stopRequested)
        {
            long startTicks = Environment.TickCount64;

            if (_backend == null)
            {
                // Бэкенд не поднялся (или потерян): пробуем пересоздать
                // раз в секунду, чтобы подсветка восстановилась сама.
                CreateBackend();
                if (_backend == null)
                {
                    Thread.Sleep(1000);
                    continue;
                }
            }

            try
            {
                if (_backend.TryCapture(_frame, TargetWidth, TargetHeight))
                {
                    _consecutiveMisses = 0;
                    FramesCaptured++;

                    var color = Average(_frame);
                    var enhanced = ColorMath.Enhance(
                        color,
                        _settings.SaturationBoost,
                        _settings.SaturationFloor,
                        _settings.ValueFloor,
                        _settings.BlackCutoff);

                    _smoothed = _smoothed is { } previous
                        ? ColorMath.Smooth(previous, enhanced, _settings.Smoothing)
                        : enhanced;

                    lock (_lock) _color = _smoothed;
                }
                else
                {
                    // Кадр не пришёл. Само по себе это нормально: DXGI
                    // молчит, когда экран не меняется, и тогда он отдаёт
                    // последний кадр из кэша. Но если настоящих кадров не
                    // было вообще, дублирование рабочего стола недоступно
                    // (ограниченная сессия, удалённый рабочий стол) -- надо
                    // переходить на резервный бэкенд.
                    _consecutiveMisses++;
                    FramesMissed++;

                    var dxgi = _backend as DxgiScreenBackend;
                    if (dxgi is { HasEverProducedFrame: true }) DxgiProducedAnyFrame = true;

                    bool dxgiBroken = dxgi is { HasEverProducedFrame: false } && _consecutiveMisses > 10;

                    if (dxgiBroken || _consecutiveMisses > 60)
                    {
                        if (dxgi != null)
                        {
                            _dxgiFailures = DxgiFailureLimit;
                            LastError = $"DXGI не отдаёт кадры ({dxgi.LastStatus}) -- перехожу на GDI";
                        }

                        _backend.Dispose();
                        _backend = null;
                        _consecutiveMisses = 0;
                        BackendRebuilds++;
                    }
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                if (_backend is DxgiScreenBackend) _dxgiFailures++;
                _backend?.Dispose();
                _backend = null;
                _consecutiveMisses = 0;
                Thread.Sleep(500);   // не долбить пересоздание в цикле
            }

            int elapsed = (int)(Environment.TickCount64 - startTicks);
            int sleep = Math.Max(0, _settings.IntervalMs - elapsed);
            if (sleep > 0) Thread.Sleep(sleep);
        }

        _backend?.Dispose();
        _backend = null;
    }

    private void CreateBackend()
    {
        // DXGI пробуем, только пока он не провалился несколько раз.
        if (_dxgiFailures < DxgiFailureLimit)
        {
            try
            {
                var dxgi = new DxgiScreenBackend();
                if (dxgi.Initialize(_settings.Monitor))
                {
                    _backend = dxgi;
                    BackendName = dxgi.Name;
                    LastError = null;
                    return;
                }

                dxgi.Dispose();
                _dxgiFailures++;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _dxgiFailures++;
            }
        }

        try
        {
            var monitor = MonitorEnumerator.Enumerate()
                .FirstOrDefault(m => m.Index == _settings.Monitor);

            if (monitor == null)
            {
                LastError = $"Монитор {_settings.Monitor} не найден";
                return;
            }

            _backend = new GdiScreenBackend(monitor.X, monitor.Y, monitor.Width, monitor.Height);
            BackendName = _dxgiFailures >= DxgiFailureLimit ? "GDI (DXGI недоступен)" : "GDI";
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _backend = null;
        }
    }

    /// <summary>
    /// Средний цвет кадра. Шаг выборки из настроек задаёт, сколько
    /// пикселей пропускать: чем больше шаг, тем дешевле расчёт.
    /// </summary>
    private Rgb Average(byte[] frame)
    {
        int step = Math.Clamp(_settings.SampleStep, 1, 16);

        long r = 0, g = 0, b = 0;
        int count = 0;

        for (int y = 0; y < TargetHeight; y += step)
        {
            int rowOffset = y * TargetWidth * 3;
            for (int x = 0; x < TargetWidth; x += step)
            {
                int i = rowOffset + x * 3;
                r += frame[i];
                g += frame[i + 1];
                b += frame[i + 2];
                count++;
            }
        }

        if (count == 0) return Rgb.Black;

        return new Rgb(
            r / (count * 255f),
            g / (count * 255f),
            b / (count * 255f));
    }

    public void Dispose() => Stop();
}
