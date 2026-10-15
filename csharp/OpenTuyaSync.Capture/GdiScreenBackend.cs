using System.Runtime.InteropServices;

namespace OpenTuyaSync.Capture;

/// <summary>Захват кадра экрана. Возвращает буфер RGB (не BGRA).</summary>
public interface IScreenBackend : IDisposable
{
    /// <summary>Имя для интерфейса и диагностики: "DXGI" или "GDI".</summary>
    string Name { get; }

    /// <summary>
    /// Захватывает кадр, уменьшенный до <paramref name="targetWidth"/> x
    /// <paramref name="targetHeight"/>, в порядке R,G,B.
    /// Возвращает false, если кадр получить не удалось (например, экран
    /// переключил режим).
    /// </summary>
    bool TryCapture(Span<byte> destination, int targetWidth, int targetHeight);
}

/// <summary>
/// Резервный захват через GDI (StretchBlt).
///
/// Работает всегда, когда есть рабочий стол, но не видит игры в
/// эксклюзивном полноэкранном режиме и медленнее. Основной сценарий --
/// страховка, если DXGI-дублирование недоступно (старые драйверы,
/// удалённый рабочий стол).
///
/// Ключевой приём производительности: кадр масштабируется сразу до
/// маленького размера прямо в GDI. Копировать 1920x1080 в память, чтобы
/// потом усреднить, -- это ~8 МБ трафика на кадр, что бессмысленно для
/// одной лампы: усреднение и так убивает детали.
/// </summary>
public sealed class GdiScreenBackend : IScreenBackend
{
    private const int SRCCOPY = 0x00CC0020;
    private const int DIB_RGB_COLORS = 0;
    private const int BI_RGB = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public uint[] bmiColors;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(
        IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool StretchBlt(
        IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
        IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, int rop);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(IntPtr hdc, int mode);

    /// <summary>HALFTONE даёт усреднение при уменьшении вместо простого
    /// отбрасывания пикселей -- цвет получается заметно точнее.</summary>
    private const int HALFTONE = 4;

    private readonly RECT _source;
    private IntPtr _screenDc;
    private IntPtr _memDc;
    private IntPtr _bitmap;
    private IntPtr _bits;
    private int _width, _height;
    private bool _disposed;

    public GdiScreenBackend(int x, int y, int width, int height)
    {
        _source = new RECT { Left = x, Top = y, Right = x + width, Bottom = y + height };
    }

    public string Name => "GDI";

    private bool EnsureBuffers(int width, int height)
    {
        if (_bits != IntPtr.Zero && _width == width && _height == height) return true;

        ReleaseBuffers();

        _screenDc = GetDC(IntPtr.Zero);
        if (_screenDc == IntPtr.Zero) return false;

        _memDc = CreateCompatibleDC(_screenDc);
        if (_memDc == IntPtr.Zero) return false;

        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                // Отрицательная высота -- строки сверху вниз, иначе
                // изображение придёт перевёрнутым.
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB,
            },
            bmiColors = new uint[256],
        };

        _bitmap = CreateDIBSection(_screenDc, ref bmi, DIB_RGB_COLORS, out _bits, IntPtr.Zero, 0);
        if (_bitmap == IntPtr.Zero || _bits == IntPtr.Zero) return false;

        SelectObject(_memDc, _bitmap);
        SetStretchBltMode(_memDc, HALFTONE);

        _width = width;
        _height = height;
        return true;
    }

    public bool TryCapture(Span<byte> destination, int targetWidth, int targetHeight)
    {
        if (_disposed) return false;
        if (destination.Length < targetWidth * targetHeight * 3) return false;
        if (!EnsureBuffers(targetWidth, targetHeight)) return false;
        if (_screenDc == IntPtr.Zero) return false;

        int srcWidth = _source.Right - _source.Left;
        int srcHeight = _source.Bottom - _source.Top;

        bool ok = StretchBlt(
            _memDc, 0, 0, targetWidth, targetHeight,
            _screenDc, _source.Left, _source.Top, srcWidth, srcHeight,
            SRCCOPY);

        if (!ok) return false;

        // GDI отдаёт BGRA -- переставляем в RGB. Читаем через
        // Marshal.Copy в промежуточный массив: буфер DIB принадлежит GDI.
        int pixelCount = targetWidth * targetHeight;
        byte[] raw = new byte[pixelCount * 4];
        Marshal.Copy(_bits, raw, 0, raw.Length);

        for (int i = 0, j = 0; i < pixelCount; i++, j += 4)
        {
            destination[i * 3] = raw[j + 2];       // R
            destination[i * 3 + 1] = raw[j + 1];   // G
            destination[i * 3 + 2] = raw[j];       // B
        }

        return true;
    }

    private void ReleaseBuffers()
    {
        if (_bitmap != IntPtr.Zero) { DeleteObject(_bitmap); _bitmap = IntPtr.Zero; }
        if (_memDc != IntPtr.Zero) { DeleteDC(_memDc); _memDc = IntPtr.Zero; }
        if (_screenDc != IntPtr.Zero) { ReleaseDC(IntPtr.Zero, _screenDc); _screenDc = IntPtr.Zero; }
        _bits = IntPtr.Zero;
        _width = _height = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseBuffers();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }
}
