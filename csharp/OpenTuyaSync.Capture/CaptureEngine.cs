using OpenTuyaSync.Core;

namespace OpenTuyaSync.Capture;

/// <summary>
/// Связывает один активный источник цвета с получателем цвета (в этой
/// архитектуре -- сервисом, который держит устройства).
///
/// Работает в отдельном потоке с фиксированным ритмом. Такт задаётся
/// настройками источника и подобран под возможности лампы: Tuya и BLE не
/// переварят 60 обновлений в секунду, а для глаза 7-10 обновлений в
/// секунду при сглаживании выглядят плавно.
/// </summary>
public sealed class CaptureEngine : IDisposable
{
    private readonly Action<Rgb> _sink;
    private readonly object _sync = new();

    private ICaptureSource? _source;
    private Thread? _thread;
    private volatile bool _stopRequested;

    private float _minV;
    private float _maxV = 1f;

    /// <summary>Пропускать отправку, если цвет почти не изменился. Экономит
    /// сеть и батарею лампы в сценах со статичной картинкой.</summary>
    private const float MinChangeThreshold = 0.004f;

    public CaptureEngine(Action<Rgb> sink) => _sink = sink;

    public bool IsRunning => _thread is { IsAlive: true };

    public SourceKind? ActiveSource { get; private set; }

    /// <summary>Количество отправленных кадров -- для статистики в UI.</summary>
    public long SentFrames { get; private set; }

    public void Start(ICaptureSource source, int sendIntervalMs, int minBrightness, int maxBrightness)
    {
        Stop();

        _source = source;
        ActiveSource = source.Kind;
        SetBrightnessLimits(minBrightness, maxBrightness);
        SentFrames = 0;

        source.Start();

        _stopRequested = false;
        _thread = new Thread(() => Run(sendIntervalMs))
        {
            IsBackground = true,
            Name = "capture-engine",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    /// <summary>
    /// Меняет пределы яркости на ходу, без перезапуска источника. Нужно,
    /// чтобы можно было сбить всплеск (флешку в игре) не останавливая
    /// подсветку.
    /// </summary>
    public void SetBrightnessLimits(int minBrightness, int maxBrightness)
    {
        lock (_sync)
        {
            _minV = Math.Clamp(minBrightness, 0, 100) / 100f;
            _maxV = Math.Clamp(maxBrightness, 0, 100) / 100f;
        }
    }

    private void Run(int sendIntervalMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long nextSend = 0;
        Rgb? lastSent = null;

        while (!_stopRequested)
        {
            Rgb? color = null;

            try
            {
                color = _source?.TryGetColor();
            }
            catch (Exception)
            {
                // Источник может быть уже освобождён или устройство
                // переключилось. Без этой защиты исключение из фонового
                // потока завершает процесс: у оконного приложения нет
                // консоли, и пользователь видит «программа вылетела».
                color = null;
            }

            if (color is { } c && sw.ElapsedMilliseconds >= nextSend)
            {
                float minV, maxV;
                lock (_sync) { minV = _minV; maxV = _maxV; }

                var clamped = ColorMath.ClampBrightness(c, minV, maxV);

                // Отправляем только заметные изменения: держать лампу в
                // курсе каждого шороха бессмысленно, а трафик и износ
                // светодиодов -- реальные.
                if (lastSent is not { } previous || previous.DistanceTo(clamped) >= MinChangeThreshold)
                {
                    try
                    {
                        _sink(clamped);
                        lastSent = clamped;
                        SentFrames++;
                    }
                    catch (Exception)
                    {
                        // Получатель недоступен (например, сервис
                        // перезапускается) -- просто пробуем снова позже.
                    }
                }

                nextSend = sw.ElapsedMilliseconds + Math.Max(20, sendIntervalMs);
            }

            // Спим короткими интервалами, чтобы остановка была отзывчивой.
            Thread.Sleep(8);
        }
    }

    /// <summary>
    /// Останавливает поток захвата.
    ///
    /// Возвращает true, если поток завершился и источник можно освобождать.
    ///
    /// Если поток не успел выйти за отведённое время, источник НЕ трогается:
    /// он ещё используется внутри нативного вызова обращения к экрану.
    /// Освобождение в этот момент -- верный способ уронить процесс в
    /// неуправляемом коде, без записи в журнал сбоев. Именно это и
    /// происходило при переключении с ручного управления на автоматический
    /// режим: старый захват останавливался, а его источник освобождался, пока
    /// поток ещё читал экран.
    /// </summary>
    public bool Stop()
    {
        _stopRequested = true;

        Thread? thread = _thread;

        if (thread is { IsAlive: true })
        {
            bool joined = thread.Join(TimeSpan.FromSeconds(3));

            if (!joined)
            {
                // Поток занят нативным вызовом. Не трогаем ни его, ни
                // источник: он завершится сам, когда вызов вернётся.
                return false;
            }

            _thread = null;
        }

        _source?.Stop();
        _source = null;
        ActiveSource = null;

        return true;
    }

    public void Dispose()
    {
        if (!Stop())
        {
            // Источник оставлен живым намеренно: поток всё ещё им
            // пользуется. Память освободит сборщик мусора вместе с
            // завершившимся потоком.
            return;
        }

        _source?.Dispose();
        _source = null;
    }
}
