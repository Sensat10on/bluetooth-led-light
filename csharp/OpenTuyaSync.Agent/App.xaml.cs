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

    /// <summary>
    /// Событие «покажи окно». Второй экземпляр выставляет его, первый
    /// открывает окно и завершает ожидание.
    ///
    /// Это страховка на случай, когда значок в трее не виден: Windows 11
    /// прячет значки новых программ в скрытую область, и тогда окно нечем
    /// вернуть. Повторный запуск ярлыка теперь открывает работающее окно
    /// вместо сообщения «уже запущено».
    /// </summary>
    private const string ShowWindowEventName = "Global\\OpenTuyaSync.Agent.ShowWindow";

    private EventWaitHandle? _showWindowSignal;
    private Thread? _showWindowThread;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Проверка единственной копии выполняется до всего остального:
        // иначе вторая копия успеет прочитать и перезаписать настройки.
        _singleInstance = new Mutex(initiallyOwned: true, SingleInstanceName, out bool isFirstInstance);

        if (!isFirstInstance)
        {
            // Вторая копия не нужна, но её запуск -- ожидаемое действие
            // пользователя: так он возвращает окно, если значок в трее
            // спрятан. Поэтому просим первую копию показать окно и тихо
            // завершаемся, без сообщений.
            try
            {
                using var signal = EventWaitHandle.OpenExisting(ShowWindowEventName);
                signal.Set();
            }
            catch (Exception)
            {
                // Первая копия не успела создать событие -- просто выходим.
            }

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

        // --enable-autostart: включает автозапуск приложения при входе в
        // систему. Используется сценарием установки, чтобы автозапуск
        // включался из одного места -- настроек приложения, а не правкой
        // реестра в командном файле. Иначе настройка в config.json и запись
        // в реестре разошлись бы: первое же сохранение настроек удалило бы
        // автозапуск, включённый сценарием.
        if (e.Args.Contains("--enable-autostart") && !config.AutoStart)
        {
            config.AutoStart = true;

            try { ConfigStore.Save(config); } catch (Exception) { }
        }

        AutoStartManager.SetEnabled(config.AutoStart);

        // --show показывает окно сразу: нужен сценарию установки, чтобы
        // пользователь увидел программу в первый раз, даже если она настроена
        // запускаться свёрнутой.
        bool forceShow = e.Args.Contains("--show");

        bool startMinimized = !forceShow &&
                              (e.Args.Contains("--minimized") || config.StartMinimized);

        _mainWindow = new MainWindow(config);

        CreateTrayIcon();
        StartShowWindowListener();

        if (!startMinimized)
            _mainWindow.Show();
        else
            _mainWindow.Hide();

        StartHeartbeat();

        // Приложение -- единственная точка запуска: оно поднимается при входе
        // в систему свёрнутым в трей и само запускает службу. Служба больше
        // не стартует вместе с Windows, иначе получался бы второй
        // независимый запуск, который некому показать в интерфейсе.
        Task.Run(() => ServiceStarter.EnsureRunning(_mainWindow));

        if (startMinimized)
            Dispatcher.BeginInvoke(new Action(ShowTrayHint));
    }

    /// <summary>
    /// Ждёт сигнала «покажи окно» от повторного запуска.
    ///
    /// Отдельный поток, потому что ожидание блокирующее, а поток интерфейса
    /// должен оставаться свободным.
    /// </summary>
    private void StartShowWindowListener()
    {
        try
        {
            _showWindowSignal = new EventWaitHandle(
                false, EventResetMode.AutoReset, ShowWindowEventName);
        }
        catch (Exception)
        {
            // Без сигнала остаётся значок в трее: это не критично.
            return;
        }

        _showWindowThread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    _showWindowSignal.WaitOne();

                    // Окно принадлежит потоку интерфейса: показывать его
                    // нужно оттуда.
                    Dispatcher.BeginInvoke(new Action(ShowWindow));
                }
                catch (Exception)
                {
                    return;
                }
            }
        })
        {
            IsBackground = true,
            Name = "show-window-listener",
        };

        _showWindowThread.Start();
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

        // Подсказка при запуске свёрнутым.
        //
        // Windows 11 по умолчанию убирает значки новых программ в скрытую
        // область панели задач, и приложение выглядит «не запустившимся».
        // Всплывающее сообщение показывает, что программа работает, и как
        // вернуть окно. Оно не мешает: показывается один раз при старте.
        _trayIcon.BalloonTipTitle = "OpenTuya Sync работает";
        _trayIcon.BalloonTipText =
            "Программа запущена в области уведомлений. Двойной щелчок по значку " +
            "откроет окно. Если значка не видно, нажмите стрелку «Показать " +
            "скрытые значки» на панели задач.";
        _trayIcon.BalloonTipIcon = System.Windows.Forms.ToolTipIcon.Info;
    }

    /// <summary>
    /// Показывает подсказку о работе в области уведомлений.
    ///
    /// Вызывается при запуске свёрнутым: без окна пользователь иначе не
    /// понимает, запустилась программа или нет. Windows 11 к тому же прячет
    /// значки новых программ в скрытую область.
    /// </summary>
    private void ShowTrayHint()
    {
        try { _trayIcon?.ShowBalloonTip(10000); }
        catch (Exception)
        {
            // Подсказка не критична.
        }
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

        try { _showWindowSignal?.Set(); } catch (Exception) { }
        try { _showWindowSignal?.Dispose(); } catch (Exception) { }

        try { _singleInstance?.ReleaseMutex(); } catch (Exception) { }
        try { _singleInstance?.Dispose(); } catch (Exception) { }

        base.OnExit(e);
    }
}
