using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace OpenTuyaSync.ColorTest;

public partial class App : Application
{
    /// <summary>Путь к журналу ошибок, рядом с программой.</summary>
    public static string CrashLogPath { get; } =
        Path.Combine(AppContext.BaseDirectory, "colortest_crash.log");

    /// <summary>Файл-признак работы: по нему видно, что программа не упала
    /// при запуске. У оконного приложения нет консоли, и без этого признака
    /// отсутствие окна ни о чём не говорит.</summary>
    public static string AlivePath { get; } =
        Path.Combine(AppContext.BaseDirectory, "colortest_alive.txt");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash(args.Exception);
            MessageBox.Show(
                $"Ошибка: {args.Exception.Message}",
                "Тест Ambilight", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash(args.ExceptionObject as Exception);

        // Режим самопроверки: отрисовывает несколько кадров всеми эффектами
        // и сохраняет их в PNG. Нужен, чтобы проверять заливку без показа
        // окна -- например, в автоматических проверках.
        if (e.Args.Contains("--selftest"))
        {
            RunSelfTest();
            return;
        }

        try
        {
            File.WriteAllText(AlivePath,
                $"запущено {DateTime.Now:yyyy-MM-dd HH:mm:ss}\nкаталог: {AppContext.BaseDirectory}\n");
        }
        catch (Exception)
        {
            // Признак работы не критичен.
        }
    }

    /// <summary>
    /// Отрисовывает кадры всеми эффектами и сохраняет их рядом с программой.
    /// Заодно проверяет, что разметка разбирается и заливка строится.
    /// </summary>
    private void RunSelfTest()
    {
        // У оконной программы нет консоли: вывод о результате пишем в файл,
        // иначе проверить результат было бы нечем.
        var report = new System.Text.StringBuilder();

        void Report(string line)
        {
            report.AppendLine(line);
            Console.WriteLine(line);
        }

        var window = new MainWindow { Width = 480, Height = 270 };
        window.Show();

        // Даём окну разложиться и запуститься таймеру.
        window.UpdateLayout();
        Pump(TimeSpan.FromMilliseconds(400));

        string[] effects = { "Переливание", "Стробоскоп", "По диагонали", "Из центра", "Вращение" };
        int failures = 0;

        Report("=== Самопроверка теста подсветки ===");
        Report($"каталог: {AppContext.BaseDirectory}");
        Report("");

        for (int i = 0; i < effects.Length; i++)
        {
            string effect = effects[i];

            try
            {
                window.SelfTestSetEffect(effect);
                Pump(TimeSpan.FromMilliseconds(250));

                var bitmap = new RenderTargetBitmap(480, 270, 96, 96,
                    System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(window);

                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));

                string file = Path.Combine(AppContext.BaseDirectory, $"selftest_{i + 1}.png");

                using var stream = File.Create(file);
                encoder.Save(stream);

                Report($"  {effect}: кадр сохранён ({Path.GetFileName(file)})");
            }
            catch (Exception ex)
            {
                failures++;
                Report($"  {effect}: ОШИБКА {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Отдельно проверяем главное свойство стробоскопа: цвет должен
        // меняться постепенно, а не скачком.
        if (!CheckStrobeSmoothness(window, Report)) failures++;

        window.Close();

        Report("");
        Report(failures == 0
            ? "РЕЗУЛЬТАТ: заливка строится всеми эффектами, стробоскоп меняет цвет плавно"
            : $"РЕЗУЛЬТАТ: ошибок {failures}");

        try
        {
            File.WriteAllText(
                Path.Combine(AppContext.BaseDirectory, "selftest_report.txt"),
                report.ToString(),
                System.Text.Encoding.UTF8);
        }
        catch (Exception)
        {
            // Отчёт не критичен для работы программы.
        }

        Shutdown(failures == 0 ? 0 : 1);
    }

    /// <summary>Прокручивает очередь сообщений заданное время.</summary>
    private static void Pump(TimeSpan duration)
    {
        var end = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < end)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            Current.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
    }

    /// <summary>
    /// Проверяет главное свойство стробоскопа: цвет меняется постепенно, а
    /// не скачком. Для этого снимаются кадры подряд и измеряется цвет в
    /// одной точке экрана -- резких скачков быть не должно.
    /// </summary>
    private static bool CheckStrobeSmoothness(MainWindow window, Action<string> report)
    {
        const int frameCount = 18;
        const int sampleStepMs = 90;

        window.SelfTestSetEffect("Стробоскоп");
        Pump(TimeSpan.FromMilliseconds(120));

        var samples = new List<(int R, int G, int B)>();

        for (int i = 0; i < frameCount; i++)
        {
            var bitmap = new RenderTargetBitmap(480, 270, 96, 96,
                System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(window);

            int stride = bitmap.PixelWidth * 4;
            var pixels = new byte[stride * bitmap.PixelHeight];
            bitmap.CopyPixels(pixels, stride, 0);

            // Точка в стороне от панели управления: там всегда чистая заливка.
            const int x = 460;
            const int y = 200;
            int offset = y * stride + x * 4;

            samples.Add((pixels[offset + 2], pixels[offset + 1], pixels[offset]));
            Pump(TimeSpan.FromMilliseconds(sampleStepMs));
        }

        report("");
        report("Стробоскоп: цвет в одной и той же точке экрана");
        report("");
        report("  кадр |   R |   G |   B | изменение");
        report("  -----+-----+-----+-----+-----------");

        int biggestJump = 0;
        int changes = 0;

        for (int i = 0; i < samples.Count; i++)
        {
            var (r, g, b) = samples[i];

            string jumpText = "";
            if (i + 1 < samples.Count)
            {
                var next = samples[i + 1];
                int jump = Math.Abs(next.R - r) + Math.Abs(next.G - g) + Math.Abs(next.B - b);

                if (jump > 5) changes++;
                biggestJump = Math.Max(biggestJump, jump);
                jumpText = jump > 5 ? $"  {jump}" : "  —";
            }

            report($"  {i + 1,4} | {r,3} | {g,3} | {b,3} |{jumpText}");
        }

        // 765 -- максимально возможная суммарная разница: полная смена
        // чёрного на белый за один кадр. Переход считается плавным, если
        // самый большой скачок далеко от этого предела.
        bool smooth = biggestJump < 200;
        bool changesAtAll = changes >= 3;

        report("");
        report($"  наибольший скачок за кадр: {biggestJump} из 765");
        report($"  кадров с изменением цвета: {changes} из {samples.Count - 1}");

        if (!changesAtAll)
            report("  ОШИБКА: цвет не меняется совсем");
        else if (!smooth)
            report("  ОШИБКА: смена цвета слишком резкая");
        else
            report("  смена цвета плавная");

        return smooth && changesAtAll;
    }

    private static void LogCrash(Exception? exception)
    {
        if (exception == null) return;

        try
        {
            File.AppendAllText(CrashLogPath,
                $"\n=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n{exception}\n");
        }
        catch (Exception)
        {
            // Если и запись не удалась, показать больше нечего.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { File.Delete(AlivePath); } catch (Exception) { }
        base.OnExit(e);
    }
}
