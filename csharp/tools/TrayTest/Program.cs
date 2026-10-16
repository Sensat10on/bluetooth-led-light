using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace OpenTuyaSync.TrayTest;

/// <summary>
/// Проверка значка в области уведомлений.
///
/// Зачем отдельная проверка. Значок в трее -- единственный способ вернуть
/// окно после сворачивания, и если он не появляется, приложение становится
/// недоступным. При этом код значка ничего не показывает на экране: окно у
/// приложения скрыто, и снаружи нельзя понять, создан значок или нет.
///
/// Поэтому проверка:
///   1. читает значок так же, как приложение, и сообщает, удалось ли;
///   2. создаёт значок и убеждается, что Windows Forms завела для него окно;
///   3. перечисляет окна процесса, чтобы это было видно, а не на словах.
///
/// Запуск: OpenTuyaSync.TrayTest.exe [секунд]
/// </summary>
internal static class Program
{
    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        int seconds = args.Length > 0 && int.TryParse(args[0], out var parsed) ? parsed : 15;

        Console.WriteLine("=== Проверка значка в области уведомлений ===");
        Console.WriteLine();

        ApplicationConfiguration.Initialize();

        // --- 1. Значок ---
        Icon? icon = TryLoadIconFile(out string iconInfo);
        Console.WriteLine($"  {iconInfo}");

        // Без файла берём системный: механика трея от этого не зависит.
        icon ??= SystemIcons.Application;

        Console.WriteLine();

        // --- 2. Создание значка ---
        var notifyIcon = new NotifyIcon
        {
            Text = "OpenTuyaSync (проверка значка)",
            Icon = icon,
            Visible = true,
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть", null, (_, _) => { });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => Application.Exit());

        notifyIcon.ContextMenuStrip = menu;

        // Сообщения Windows Forms нужно прокачать: без этого окно значка не
        // создаётся, и значок не появляется.
        Pump(1200);

        Console.WriteLine("  значок создан. Окна процесса:");
        foreach (string window in ListNotifyIconWindows())
            Console.WriteLine($"    {window}");

        int iconWindows = ListNotifyIconWindows().Count;
        Console.WriteLine($"  окон значка найдено: {iconWindows}");

        if (iconWindows == 0)
        {
            Console.WriteLine();
            Console.WriteLine("  ВНИМАНИЕ: окно значка не создано -- значок не зарегистрирован.");
            notifyIcon.Dispose();
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine($"  значок будет виден {seconds} с.");
        Console.WriteLine("  Посмотрите в области уведомлений панели задач:");
        Console.WriteLine("  при наведении мыши должна быть надпись");
        Console.WriteLine("  «OpenTuyaSync (проверка значка)». Если значка не видно,");
        Console.WriteLine("  нажмите на панели задач стрелку «Показать скрытые значки».");
        Console.WriteLine();

        Pump(seconds * 1000);

        Console.WriteLine("  скрываю значок...");
        notifyIcon.Visible = false;
        Pump(800);

        Console.WriteLine($"  окон значка после скрытия: {ListNotifyIconWindows().Count}");

        notifyIcon.Dispose();
        Pump(300);

        Console.WriteLine();
        Console.WriteLine("РЕЗУЛЬТАТ: проверка завершена.");
        return 0;
    }

    /// <summary>
    /// Читает значок так же, как приложение: файл рядом с программой, иначе
    /// ничего. Возвращает и текст с итогом -- чтобы причину было видно.
    /// </summary>
    private static Icon? TryLoadIconFile(out string info)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "icon.ico");

        if (!File.Exists(path))
        {
            info = $"файла значка нет: {path} (беру системный)";
            return null;
        }

        try
        {
            var icon = new Icon(path);
            info = $"значок прочитан: {path} ({icon.Width}x{icon.Height})";
            return icon;
        }
        catch (Exception ex)
        {
            info = $"значок не прочитан ({ex.GetType().Name}: {ex.Message}), беру системный";
            return null;
        }
    }

    /// <summary>
    /// Окна области уведомлений этого процесса.
    ///
    /// Windows Forms заводит для значка скрытое окно. Если оно есть, значок
    /// зарегистрирован в системе; если нет -- значок не создавался.
    /// </summary>
    private static List<string> ListNotifyIconWindows()
    {
        var result = new List<string>();
        uint current = (uint)Environment.ProcessId;

        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint processId);

            if (processId != current) return true;

            var name = new StringBuilder(256);
            GetClassName(window, name, name.Capacity);

            // Имя класса окна значка содержит "WindowsForms10".
            if (name.ToString().Contains("WindowsForms10"))
                result.Add($"class={name}");

            return true;
        }, IntPtr.Zero);

        return result;
    }

    /// <summary>
    /// Прокачивает очередь сообщений заданное время.
    ///
    /// Без этого значка не будет: Windows Forms обрабатывает сообщения в
    /// цикле, и при простом ожидании окно значка не создаётся.
    /// </summary>
    private static void Pump(int milliseconds)
    {
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.ElapsedMilliseconds < milliseconds)
        {
            Application.DoEvents();
            Thread.Sleep(50);
        }
    }
}
