using OpenTuyaSync.Capture;

// Диагностика DXGI Desktop Duplication: показывает адаптеры, поднимает
// захват и проверяет, приходят ли кадры. Нужна, когда подсветка работает
// не через DXGI, а через резервный GDI.
//
// Запуск: OpenTuyaSync.DxgiProbe.exe [монитор] [кадров]

Console.OutputEncoding = System.Text.Encoding.UTF8;

int monitor = args.Length > 0 && int.TryParse(args[0], out var m) ? m : 1;
int frames = args.Length > 1 && int.TryParse(args[1], out var f) ? f : 30;

Console.WriteLine("=== Адаптеры ===");
foreach (string line in DxgiScreenBackend.DescribeAdapters())
    Console.WriteLine("  " + line);

Console.WriteLine();
Console.WriteLine($"=== Проверка захвата (монитор {monitor}) ===");

using var backend = new DxgiScreenBackend();

if (!backend.Initialize(monitor))
{
    Console.Error.WriteLine($"  Инициализация не удалась: {backend.LastStatus}");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Приложение в этом случае автоматически переходит на резервный");
    Console.Error.WriteLine("захват GDI: подсветка работает, но игры в полноэкранном режиме");
    Console.Error.WriteLine("могут не попадать в кадр.");
    return 2;
}

Console.WriteLine($"  захват поднят: {backend.AdapterName}");

byte[] frame = new byte[64 * 36 * 3];
int captured = 0, misses = 0;

for (int i = 0; i < frames; i++)
{
    if (backend.TryCapture(frame, 64, 36)) captured++;
    else misses++;
    Thread.Sleep(50);
}

long r = 0, g = 0, b = 0;
int count = frame.Length / 3;
for (int i = 0; i < frame.Length; i += 3)
{
    r += frame[i]; g += frame[i + 1]; b += frame[i + 2];
}

Console.WriteLine($"  кадров получено: {captured}, пропущено: {misses}");
Console.WriteLine($"  средний цвет кадра: RGB({r / count}, {g / count}, {b / count})");
Console.WriteLine($"  состояние: {backend.LastStatus}");
Console.WriteLine();

if (captured > 0)
{
    Console.WriteLine("Вывод: Desktop Duplication работает. Подсветка будет следовать");
    Console.WriteLine("за играми в полноэкранном режиме.");
    return 0;
}

Console.WriteLine("Вывод: кадры не приходят. Проверьте драйвер видеокарты;");
Console.WriteLine("приложение при этом продолжит работать через GDI.");
return 1;
