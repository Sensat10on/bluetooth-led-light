using System.Diagnostics;
using OpenTuyaSync.Capture;
using OpenTuyaSync.Core;

// Диагностика захвата экрана: проверяет, работает ли DXGI Desktop
// Duplication (от него зависит подсветка в полноэкранных играх) и сколько
// процессорного времени съедает захват. Это главный показатель того,
// будет ли приложение мешать игре.
//
// Запуск: OpenTuyaSync.CaptureTest.exe [монитор] [секунды]

Console.OutputEncoding = System.Text.Encoding.UTF8;

int monitor = args.Length > 0 && int.TryParse(args[0], out var m) ? m : 1;
int seconds = args.Length > 1 && int.TryParse(args[1], out var s) ? s : 5;

Console.WriteLine("=== Мониторы ===");
var monitors = MonitorEnumerator.Enumerate();
foreach (var info in monitors)
    Console.WriteLine($"  {info.Caption}   ({info.Width}x{info.Height} @ {info.X},{info.Y})");

Console.WriteLine();
Console.WriteLine($"=== DXGI доступен: {DxgiScreenBackend.IsAvailable()} ===");

// --- Прямая проверка бэкендов -------------------------------------------
foreach (string backendName in new[] { "DXGI", "GDI" })
{
    Console.WriteLine();
    Console.WriteLine($"=== Проверка {backendName} ===");

    using IScreenBackend backend = backendName == "DXGI"
        ? new DxgiScreenBackend()
        : CreateGdi(monitor);

    bool ready = backend switch
    {
        DxgiScreenBackend dxgi => dxgi.Initialize(monitor),
        _ => true,
    };

    if (!ready)
    {
        Console.WriteLine($"  {backendName}: инициализация не удалась (недоступен на этой машине)");
        continue;
    }

    byte[] frame = new byte[64 * 36 * 3];
    int captured = 0;
    var sw = Stopwatch.StartNew();

    for (int i = 0; i < 60; i++)
    {
        if (backend.TryCapture(frame, 64, 36)) captured++;
        Thread.Sleep(8);
    }

    sw.Stop();

    if (backend is DxgiScreenBackend dxgiBackend)
        Console.WriteLine($"  состояние DXGI: {dxgiBackend.LastStatus}");

    if (captured == 0)
    {
        Console.WriteLine($"  {backendName}: ни одного кадра не получено");
        continue;
    }

    long r = 0, g = 0, b = 0;
    for (int i = 0; i < frame.Length; i += 3)
    {
        r += frame[i]; g += frame[i + 1]; b += frame[i + 2];
    }
    int count = frame.Length / 3;

    Console.WriteLine($"  кадров получено: {captured}/60 за {sw.ElapsedMilliseconds} мс");
    Console.WriteLine($"  средний цвет последнего кадра: RGB({r / count}, {g / count}, {b / count})");
}

// --- Замер процессорного времени источника ------------------------------
Console.WriteLine();
Console.WriteLine($"=== Источник Ambilight целиком ({seconds} с, частота 150 мс) ===");

var settings = new ScreenSettings { Monitor = monitor, IntervalMs = 150 };
using var source = new ScreenCaptureSource(settings);

var process = Process.GetCurrentProcess();
TimeSpan cpuBefore = process.TotalProcessorTime;
var wallWatch = Stopwatch.StartNew();

source.Start();

Rgb? last = null;
int colorChanges = 0;
while (wallWatch.Elapsed.TotalSeconds < seconds)
{
    var color = source.TryGetColor();
    if (color is { } c)
    {
        if (last is { } previous && previous.DistanceTo(c) > 0.01f) colorChanges++;
        last = c;
    }
    Thread.Sleep(50);
}

source.Stop();
wallWatch.Stop();

TimeSpan cpuAfter = Process.GetCurrentProcess().TotalProcessorTime;
double cpuMs = (cpuAfter - cpuBefore).TotalMilliseconds;
double cpuPercent = cpuMs / (wallWatch.Elapsed.TotalMilliseconds * Environment.ProcessorCount) * 100.0;

Console.WriteLine($"  бэкенд: {source.BackendName}");
Console.WriteLine($"  кадров получено: {source.FramesCaptured}, пропущено: {source.FramesMissed}, " +
                  $"пересозданий бэкенда: {source.BackendRebuilds}, DXGI отдавал кадры: {source.DxgiProducedAnyFrame}");
Console.WriteLine($"  последний цвет: {last}");
Console.WriteLine($"  изменений цвета: {colorChanges}");
Console.WriteLine($"  ошибка: {source.LastError ?? "нет"}");
Console.WriteLine();
Console.WriteLine($"  ПРОЦЕССОРНОЕ ВРЕМЯ ЗАХВАТА: {cpuMs:0} мс за {wallWatch.Elapsed.TotalSeconds:0.0} с");
Console.WriteLine($"  ЭТО {cpuPercent:0.00}% от одного ядра (при {Environment.ProcessorCount} ядрах)");

Console.WriteLine();
Console.WriteLine("Вывод: доля процессора ниже 1% одного ядра означает, что захват");
Console.WriteLine("не мешает игре. Основное время уходит на копирование кадра из");
Console.WriteLine("видеопамяти, поэтому размер выборки 64x36 -- ключевой параметр.");

return 0;

static IScreenBackend CreateGdi(int monitorIndex)
{
    var info = MonitorEnumerator.Enumerate().First(m => m.Index == monitorIndex);
    return new GdiScreenBackend(info.X, info.Y, info.Width, info.Height);
}
