using System.Windows;
using OpenTuyaSync.Core;

namespace OpenTuyaSync.Agent;

/// <summary>
/// Точка входа приложения-агента.
///
/// Агент работает в сессии пользователя и отвечает за две вещи: интерфейс
/// и захват экрана. Управление устройствами живёт в службе Windows --
/// именно потому, что служба не может захватывать экран (сессия 0), а
/// агент не может работать без входа пользователя в систему.
/// </summary>
public partial class App : Application
{
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private MainWindow? _mainWindow;

    /// <summary>
    /// Признак единственной копии. Две запущенные копии перезаписывают
    /// общий config.json и мешают друг другу в списке устройств, поэтому
    /// вторая копия сразу завершается.
    /// </summary>
    private Mutex? _singleInstance;

    private const string SingleInstanceName = "Global\\OpenTuyaSync.Agent.SingleInstance";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Проверка единственной копии выполняется до всего остального:
        // иначе вторая копия успеет прочитать и перезаписать настройки.
        _singleInstance = new Mutex(initiallyOwned: true, SingleInstanceName, out bool isFirstInstance);

        if (!isFirstInstance)
        {
            MessageBox.Show(
                "OpenTuya Sync уже запущен.\n\n" +
                "Вторая копия не нужна: она мешает первой и может испортить настройки. " +
                "Окно уже запущенного приложения можно открыть двойным щелчком по значку " +
                "в области уведомлений.",
                "OpenTuya Sync", MessageBoxButton.OK, MessageBoxImage.Information);

            Shutdown();
            return;
        }

        // У оконного приложения нет консоли: без перехвата исключение при
        // запуске выглядит как «окно не появилось» без единой подсказки.
        // Поэтому всё пишется в файл рядом с приложением.
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash(args.Exception);
            MessageBox.Show(
                $"Произошла ошибка:\n\n{args.Exception.Message}\n\nПодробности: {CrashLogPath}",
                "OpenTuya Sync", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash(args.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash(args.Exception);
            args.SetObserved();
        };

        // Каталог данных рядом с исполняемым файлом: и служба, и агент
        // должны видеть один и тот же config.json.
        ConfigStore.DataDirectory = AppContext.BaseDirectory;

        // Конфигурация читается один раз и передаётся в окно: повторное
        // чтение того же файла на старте -- лишняя работа.
        var config = ConfigStore.Load();

        bool startMinimized = e.Args.Contains("--minimized") || config.StartMinimized;

        _mainWindow = new MainWindow(config);

        CreateTrayIcon();

        if (!startMinimized)
            _mainWindow.Show();
        else
            _mainWindow.Hide();

        StartHeartbeat();
    }

    /// <summary>
    /// Периодически обновляет файл-признак работы. Нужен для диагностики:
    /// у оконного приложения нет консоли, и по одному лишь отсутствию окна
    /// нельзя понять, упало оно при запуске или просто свёрнуто в трей.
    /// Файл со свежим временем означает, что приложение живо.
    /// </summary>
    private void StartHeartbeat()
    {
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5),
        };

        timer.Tick += (_, _) =>
        {
            try
            {
                File.WriteAllText(HeartbeatPath,
                    $"работает с {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                    $"версия: {typeof(App).Assembly.GetName().Version}\n" +
                    $"каталог: {AppContext.BaseDirectory}\n");
            }
            catch (Exception)
            {
                // Файл-признак не критичен для работы.
            }
        };

        timer.Start();
    }

    private static string HeartbeatPath =>
        Path.Combine(AppContext.BaseDirectory, "agent_alive.txt");

    private static string CrashLogPath =>
        Path.Combine(AppContext.BaseDirectory, "agent_crash.log");

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

    /// <summary>
    /// Значок в области уведомлений. Создаётся через Windows Forms:
    /// у WPF нет собственного значка трея, а тащить ради него отдельную
    /// библиотеку смысла нет.
    /// </summary>
    private void CreateTrayIcon()
    {
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "OpenTuya Sync",
            Visible = true,
            Icon = LoadTrayIcon(),
        };

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Открыть", null, (_, _) => ShowWindow());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitApplication());

        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => ShowWindow();
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            string iconPath = Path.Combine(AppContext.BaseDirectory, "icon.ico");
            if (File.Exists(iconPath))
                return new System.Drawing.Icon(iconPath);
        }
        catch (Exception)
        {
            // Значок не критичен: если файла нет, берём системный.
        }

        return System.Drawing.SystemIcons.Application;
    }

    public void ShowWindow()
    {
        if (_mainWindow == null) return;

        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    public void ExitApplication()
    {
        _mainWindow?.Shutdown();
        _trayIcon?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { File.Delete(HeartbeatPath); } catch (Exception) { }
        _trayIcon?.Dispose();

        try { _singleInstance?.ReleaseMutex(); } catch (Exception) { }
        try { _singleInstance?.Dispose(); } catch (Exception) { }

        base.OnExit(e);
    }
}
