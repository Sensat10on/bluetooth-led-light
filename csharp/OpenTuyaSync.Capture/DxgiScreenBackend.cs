using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using OpenTuyaSync.Core;
using Device = SharpDX.Direct3D11.Device;
using Resource = SharpDX.Direct3D11.Resource;

namespace OpenTuyaSync.Capture;

/// <summary>
/// Захват экрана через DXGI Desktop Duplication — основной путь, потому что
/// он работает и с окнами, и с играми в полноэкранном режиме (Windows
/// отдаёт их как независимый флип-вывод, который этот API видит), и не
/// читает кадр из видеопамяти процессором так, как это делает GDI.
///
/// Схема: получаем кадр в текстуру видеопамяти, копируем её в staging-
/// текстуру (память, доступную процессору) и читаем строки прямо в
/// маленький буфер с одновременным уменьшением.
///
/// Для работы с COM используется SharpDX. Попытка обойтись ручными
/// объявлениями интерфейсов закончилась тем, что DuplicateOutput проходил,
/// а AcquireNextFrame всегда отвечал DXGI_ERROR_UNSUPPORTED: в самодельных
/// объявлениях неизбежно расходятся порядок методов и сигнатуры, а COM
/// такие ошибки не прощает.
/// </summary>
public sealed class DxgiScreenBackend : IScreenBackend
{
    private Factory1? _factory;
    private Adapter1? _adapter;
    private Device? _device;
    private Output1? _output;
    private OutputDuplication? _duplication;
    private Texture2D? _staging;
    private int _stagingWidth, _stagingHeight;
    private bool _disposed;
    private bool _frameAcquired;

    /// <summary>
    /// Кэш последнего кадра. Desktop Duplication отдаёт новый кадр только
    /// при изменении экрана, поэтому на статичной картинке AcquireNextFrame
    /// сообщает «таймаут». Это не ошибка: подсветка обязана продолжать
    /// работать, поэтому последний кадр запоминается и отдаётся повторно.
    /// </summary>
    private byte[]? _lastFrame;
    private int _lastFrameLength;

    public string Name => "DXGI";

    /// <summary>Описание адаптера, на котором поднят захват.</summary>
    public string AdapterName { get; private set; } = "";

    /// <summary>Последний результат работы — для диагностики.</summary>
    public string LastStatus { get; private set; } = "ещё не вызывался";

    /// <summary>Был ли получен хотя бы один настоящий кадр.</summary>
    public bool HasEverProducedFrame { get; private set; }

    /// <summary>Есть ли на этой машине подходящий адаптер с выводом.</summary>
    public static bool IsAvailable()
    {
        try
        {
            using var factory = new Factory1();
            for (int i = 0; ; i++)
            {
                Adapter1? adapter = null;
                try { adapter = factory.GetAdapter1(i); }
                catch (SharpDXException) { break; }

                if (adapter == null) break;

                using (adapter)
                {
                    if (adapter.GetOutputCount() > 0) return true;
                }
            }
        }
        catch (Exception)
        {
            return false;
        }

        return false;
    }

    /// <summary>Список адаптеров для диагностики.</summary>
    public static List<string> DescribeAdapters()
    {
        var lines = new List<string>();

        try
        {
            using var factory = new Factory1();
            for (int i = 0; ; i++)
            {
                Adapter1? adapter = null;
                try { adapter = factory.GetAdapter1(i); }
                catch (SharpDXException) { break; }

                if (adapter == null) break;

                using (adapter)
                {
                    lines.Add($"[{i}] {adapter.Description1.Description}  " +
                              $"выходов: {adapter.GetOutputCount()}");
                }
            }
        }
        catch (Exception ex)
        {
            lines.Add($"ошибка перечисления: {ex.Message}");
        }

        return lines;
    }

    public bool Initialize(int monitorIndex)
    {
        try
        {
            _factory = new Factory1();

            // Индекс монитора в интерфейсе: 0 -- все мониторы, 1..N --
            // физические. Для DXGI это всегда выход 0 подходящего адаптера.
            int wantedOutput = 0;

            // Ищем адаптер, у которого вообще есть выходы. Адаптер
            // «Microsoft Basic Render Driver» их не имеет, и попытка
            // создать на нём устройство даёт как раз
            // DXGI_ERROR_UNSUPPORTED.
            for (int i = 0; ; i++)
            {
                Adapter1? candidate = null;
                try { candidate = _factory.GetAdapter1(i); }
                catch (SharpDXException) { break; }

                if (candidate == null) break;

                if (candidate.GetOutputCount() <= wantedOutput)
                {
                    candidate.Dispose();
                    continue;
                }

                _adapter = candidate;
                break;
            }

            if (_adapter == null)
            {
                LastStatus = "не найден адаптер с подключённым монитором";
                return false;
            }

            AdapterName = _adapter.Description1.Description;

            // Устройство обязательно создаётся на том же адаптере, что
            // владеет монитором.
            _device = new Device(_adapter, DeviceCreationFlags.BgraSupport);

            using var output = _adapter.GetOutput(wantedOutput);
            _output = output.QueryInterface<Output1>();

            _duplication = _output.DuplicateOutput(_device);

            LastStatus = $"захват поднят на {AdapterName}";
            return true;
        }
        catch (Exception ex)
        {
            LastStatus = $"инициализация: {ex.Message}";
            Dispose();
            return false;
        }
    }

    public bool TryCapture(Span<byte> destination, int targetWidth, int targetHeight)
    {
        if (_disposed || _duplication == null || _device == null) return false;

        try
        {
            // Ждём кадр до 50 мс. С нулевым таймаутом дублирование часто
            // отвечает «не готово» даже когда экран меняется, и подсветка
            // получала бы кадры рывками.
            var result = _duplication.TryAcquireNextFrame(50, out var frameInfo, out var desktopResource);

            if (!result.Success)
            {
                // Код 0x887A0027 -- «кадр не изменился с прошлого раза».
                const int WaitTimeoutCode = unchecked((int)0x887A0027);

                if (result.Code == WaitTimeoutCode)
                {
                    // Экран не изменился -- отдаём последний кадр из кэша,
                    // иначе статичная картинка гасила бы лампу.
                    return ReuseLastFrame(destination);
                }

                LastStatus = $"AcquireNextFrame: 0x{result.Code:X8}";
                return false;
            }

            _frameAcquired = true;

            if (!EnsureStaging(targetWidth, targetHeight)) return false;

            // desktopResource приходит из DXGI, а CopyResource ждёт ресурс
            // Direct3D11 -- это один и тот же объект, нужно лишь привести
            // тип.
            using (var source = desktopResource.QueryInterface<SharpDX.Direct3D11.Resource>())
            {
                _device.ImmediateContext.CopyResource(source, _staging);
            }

            var box = _device.ImmediateContext.MapSubresource(
                _staging, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);

            try
            {
                CopyAndConvert(box, destination, targetWidth, targetHeight);
            }
            finally
            {
                _device.ImmediateContext.UnmapSubresource(_staging, 0);
            }

            RememberFrame(destination);
            HasEverProducedFrame = true;
            LastStatus = "кадр получен";
            return true;
        }
        catch (Exception ex)
        {
            // Потеря доступа к выводу: смена разрешения, переход игры в
            // другой режим. Вызывающий код пересоздаст бэкенд.
            LastStatus = $"исключение: {ex.Message}";
            return false;
        }
        finally
        {
            // Сообщаем DXGI, что кадр обработан. Пропуск этого вызова
            // приводит к тому, что следующий AcquireNextFrame отвечает
            // DXGI_ERROR_INVALID_CALL.
            if (_frameAcquired)
            {
                try { _duplication.ReleaseFrame(); } catch (Exception) { }
                _frameAcquired = false;
            }
        }
    }

    /// <summary>
    /// Копирует staging-текстуру в плоский буфер RGB с уменьшением.
    /// Делается в один проход: лишний проход по нескольким мегабайтам на
    /// кадр заметен на слабых машинах.
    /// </summary>
    private void CopyAndConvert(DataBox box, Span<byte> destination, int targetWidth, int targetHeight)
    {
        int sourceWidth = _stagingWidth;
        int sourceHeight = _stagingHeight;

        int xStep = Math.Max(1, sourceWidth / targetWidth);
        int yStep = Math.Max(1, sourceHeight / targetHeight);

        unsafe
        {
            byte* basePtr = (byte*)box.DataPointer;

            for (int ty = 0; ty < targetHeight; ty++)
            {
                int sy = Math.Min(ty * yStep, sourceHeight - 1);
                byte* row = basePtr + (long)sy * box.RowPitch;

                for (int tx = 0; tx < targetWidth; tx++)
                {
                    int sx = Math.Min(tx * xStep, sourceWidth - 1);
                    byte* pixel = row + (long)sx * 4;
                    int outIndex = (ty * targetWidth + tx) * 3;

                    // BGRA -> RGB
                    destination[outIndex] = pixel[2];
                    destination[outIndex + 1] = pixel[1];
                    destination[outIndex + 2] = pixel[0];
                }
            }
        }
    }

    private void RememberFrame(Span<byte> frame)
    {
        if (_lastFrame == null || _lastFrame.Length != frame.Length)
            _lastFrame = new byte[frame.Length];

        frame.CopyTo(_lastFrame);
        _lastFrameLength = frame.Length;
    }

    private bool ReuseLastFrame(Span<byte> destination)
    {
        if (_lastFrame == null || _lastFrameLength == 0) return false;
        if (destination.Length < _lastFrameLength) return false;

        _lastFrame.AsSpan(0, _lastFrameLength).CopyTo(destination);
        return true;
    }

    private bool EnsureStaging(int width, int height)
    {
        if (_staging != null && _stagingWidth == width && _stagingHeight == height) return true;

        _staging?.Dispose();
        _staging = null;

        var description = new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CpuAccessFlags = CpuAccessFlags.Read,
            OptionFlags = ResourceOptionFlags.None,
        };

        try
        {
            _staging = new Texture2D(_device, description);
            _stagingWidth = width;
            _stagingHeight = height;
            return true;
        }
        catch (Exception ex)
        {
            LastStatus = $"staging-текстура: {ex.Message}";
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Освобождение обёрнуто в try/catch: если драйвер уже сломал
        // состояние (например, вывод пропал при смене разрешения), Dispose
        // может бросить исключение, а оно в финализаторе роняет процесс.
        try { _staging?.Dispose(); } catch (Exception) { }
        try { _duplication?.Dispose(); } catch (Exception) { }
        try { _output?.Dispose(); } catch (Exception) { }
        try { _device?.Dispose(); } catch (Exception) { }
        try { _adapter?.Dispose(); } catch (Exception) { }
        try { _factory?.Dispose(); } catch (Exception) { }

        _staging = null;
        _duplication = null;
        _output = null;
        _device = null;
        _adapter = null;
        _factory = null;
        _lastFrame = null;
        _lastFrameLength = 0;
    }
}
