using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;
using OpenTuyaSync.Core;

namespace OpenTuyaSync.Capture;

/// <summary>
/// Режим музыки: реагирует на то, что играет в колонках, через WASAPI
/// loopback (не микрофон). Автоусиление подстраивается под реальную
/// громкость вместо фиксированного порога, плюс простое определение
/// удара баса для смены оттенка.
///
/// Чтение звука и расчёт цвета намеренно разведены по разным потокам:
/// совмещение давало разрывы звука, когда отправка на лампу подтормаживала
/// (в Python-версии это воспроизводилось и было исправлено именно так).
/// </summary>
public sealed class MusicCaptureSource : ICaptureSource
{
    private const int FftLength = 1024;
    private const int FftExponent = 10;          // 2^10 == 1024
    private const double BassHz = 200.0;

    private readonly MusicSettings _settings;
    private readonly object _lock = new();
    private readonly AutoGain _gain = new();
    private readonly BeatDetector _beats;

    private readonly Complex[] _fftBuffer = new Complex[FftLength];
    private readonly float[] _monoBuffer = new float[FftLength];
    private readonly double[] _window = new double[FftLength];

    private WasapiLoopbackCapture? _capture;
    private Thread? _analysisThread;
    private volatile bool _stopRequested;
    private volatile bool _audioAvailable;

    private Rgb? _color;
    private double _hue;

    /// <summary>Последний блок звука, заполняется потоком захвата.</summary>
    private float[] _latestBlock = new float[FftLength];
    private int _latestCount;

    public MusicCaptureSource(MusicSettings settings)
    {
        _settings = settings;
        _beats = new BeatDetector(settings.BeatFactor, settings.BeatCooldown);

        // Окно Ханна: без него на границах блока возникают ложные
        // высокочастотные составляющие, и бас «размазывается».
        for (int i = 0; i < FftLength; i++)
            _window[i] = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (FftLength - 1)));
    }

    public SourceKind Kind => SourceKind.Music;

    public string Caption => "Режим музыки (звук)";

    public bool IsRunning { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Имя устройства, с которого снимается звук.</summary>
    public string DeviceName { get; private set; } = "";

    public void Start()
    {
        if (IsRunning) return;

        _stopRequested = false;
        LastError = null;
        _gain.Reset();
        _beats.Reset();

        try
        {
            var device = ResolveDevice();
            _capture = new WasapiLoopbackCapture(device);
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += (_, e) =>
            {
                if (e.Exception != null) LastError = e.Exception.Message;
            };
            _capture.StartRecording();

            _audioAvailable = true;
            DeviceName = device.FriendlyName;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _audioAvailable = false;
        }

        _analysisThread = new Thread(Analyze)
        {
            IsBackground = true,
            Name = "music-analysis",
            Priority = ThreadPriority.BelowNormal,
        };
        _analysisThread.Start();
        IsRunning = true;
    }

    /// <summary>
    /// Устройство вывода: либо выбранное в настройках, либо системное по
    /// умолчанию. Явный выбор нужен, когда звук уходит на наушники, а
    /// колонки остаются устройством по умолчанию.
    /// </summary>
    private MMDevice ResolveDevice()
    {
        using var enumerator = new MMDeviceEnumerator();

        if (!string.IsNullOrWhiteSpace(_settings.OutputDevice))
        {
            foreach (var candidate in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                if (candidate.FriendlyName == _settings.OutputDevice)
                    return candidate;
            }
        }

        return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    /// <summary>
    /// Список устройств вывода для настроек.
    /// </summary>
    public static List<string> ListOutputDevices()
    {
        var result = new List<string>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                result.Add(device.FriendlyName);
        }
        catch (Exception)
        {
            // Нет доступных устройств -- вернём пустой список.
        }
        return result;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        // Вызывается из потока захвата. Здесь нельзя считать FFT: любая
        // задержка в этом обработчике приводит к потере аудиобуферов.
        // Копируем максимум один блок и уходим.
        int available = e.BytesRecorded / sizeof(float);
        if (available < FftLength) return;

        var format = _capture?.WaveFormat;
        if (format == null) return;

        int channels = Math.Max(1, format.Channels);

        lock (_lock)
        {
            Buffer.BlockCopy(e.Buffer, 0, _latestBlock, 0, FftLength * sizeof(float));
            _latestCount = FftLength;

            // Если формат многоканальный, сводим в моно прямо здесь --
            // операция дешёвая, а в анализирующем потоке сэкономит проход.
            if (channels > 1)
            {
                int frames = FftLength / channels;
                for (int i = 0; i < frames; i++)
                {
                    float sum = 0f;
                    for (int c = 0; c < channels; c++)
                        sum += _latestBlock[i * channels + c];
                    _monoBuffer[i] = sum / channels;
                }
                _latestCount = frames;
            }
            else
            {
                Array.Copy(_latestBlock, _monoBuffer, FftLength);
            }
        }
    }

    private void Analyze()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        while (!_stopRequested)
        {
            if (!_audioAvailable)
            {
                Thread.Sleep(200);
                continue;
            }

            float[] mono;
            int count;
            lock (_lock)
            {
                mono = new float[_latestCount];
                Array.Copy(_monoBuffer, mono, _latestCount);
                count = _latestCount;
            }

            if (count < 64)
            {
                Thread.Sleep(15);
                continue;
            }

            double rms = 0;
            for (int i = 0; i < count; i++) rms += mono[i] * mono[i];
            rms = Math.Sqrt(rms / count);

            double bassEnergy = ComputeBassEnergy(mono, count);

            double now = sw.Elapsed.TotalSeconds;
            float level = _gain.Normalize((float)rms);
            bool isBeat = _beats.Check((float)bassEnergy, now);

            float s, v;
            if (level < 0.03f)
            {
                s = 0f; v = 0f;                       // тишина -- гасим
            }
            else if (isBeat)
            {
                _hue = (_hue + 35.0) % 360.0;         // удар -- рывок по кругу
                s = 1f; v = 1f;
            }
            else
            {
                _hue = (_hue + 0.5) % 360.0;          // медленный дрейф оттенка
                s = 1f;
                v = Math.Max(_settings.MinBrightness, Math.Min(1f, level));
            }

            lock (_lock) _color = Rgb.FromHsv((float)_hue, s, v);

            Thread.Sleep(15);
        }
    }

    /// <summary>
    /// Суммарная энергия ниже 200 Гц. Считается через FFT с окном Ханна.
    /// </summary>
    private double ComputeBassEnergy(float[] mono, int count)
    {
        int length = Math.Min(count, FftLength);

        for (int i = 0; i < length; i++)
        {
            _fftBuffer[i].X = (float)(mono[i] * _window[i]);
            _fftBuffer[i].Y = 0f;
        }

        // Добиваем нулями: FastFourierTransform требует ровно 2^n точек.
        for (int i = length; i < FftLength; i++)
        {
            _fftBuffer[i].X = 0f;
            _fftBuffer[i].Y = 0f;
        }

        FastFourierTransform.FFT(true, FftExponent, _fftBuffer);

        int sampleRate = _capture?.WaveFormat.SampleRate ?? 48000;
        double binHz = (double)sampleRate / FftLength;
        int bassBins = Math.Max(1, (int)(BassHz / binHz));

        double energy = 0;
        for (int i = 1; i <= bassBins && i < FftLength / 2; i++)
        {
            float re = _fftBuffer[i].X;
            float im = _fftBuffer[i].Y;
            energy += Math.Sqrt(re * re + im * im);
        }

        return energy;
    }

    public Rgb? TryGetColor()
    {
        lock (_lock) return _color;
    }

    public void Stop()
    {
        _stopRequested = true;

        try { _capture?.StopRecording(); } catch (Exception) { }

        if (_analysisThread is { IsAlive: true })
        {
            _analysisThread.Join(TimeSpan.FromSeconds(1));
            _analysisThread = null;
        }

        try
        {
            if (_capture != null)
            {
                _capture.DataAvailable -= OnDataAvailable;
                _capture.Dispose();
            }
        }
        catch (Exception) { }

        _capture = null;
        IsRunning = false;
    }

    public void Dispose() => Stop();
}
