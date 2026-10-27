using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OpenTuyaSync.Capture;
using OpenTuyaSync.Core;
using OpenTuyaSync.Core.Ipc;

namespace OpenTuyaSync.Agent;

public partial class MainWindow : Window
{
    private readonly ServiceClient _service = new();

    /// <summary>Bluetooth-ленты: обслуживаются приложением, а не службой,
    /// потому что служба работает в сессии 0 без доступа к Bluetooth.</summary>
    private readonly LocalBleDevices _localBle = new();
    private readonly DispatcherTimer _statusTimer;

    private AppConfig _config;
    private CaptureEngine? _engine;
    private HotkeyManager? _hotkeys;
    private ICaptureSource? _currentSource;

    private DeviceConfig? _selectedDevice;
    private DiscoveredDevice? _lastScanResult;
    private CancellationTokenSource? _scanCts;

    private string _capturingHotkey = "";
    private Rgb _manualColor = new(1f, 1f, 1f);

    private bool _loadingUi;

    /// <summary>
    /// Признак программного изменения ползунков: цвет из палитры уже
    /// отправлен, и обработчик ползунков не должен отправлять его повторно.
    /// </summary>
    private bool _suppressManualSend;
    public MainWindow(AppConfig config)
    {
        InitializeComponent();

        _config = config;

        // Неудача подключения Bluetooth-ленты приходит из фонового потока:
        // без этого сообщения пользователь видел только бесконечное
        // «Bluetooth: подключение...» и не знал причины.
        _localBle.ConnectFailed += OnBleConnectFailed;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();

        Loaded += OnWindowLoaded;
        Closing += OnWindowClosing;
    }

    /// <summary>
    /// Подключиться к Bluetooth-ленте не удалось.
    ///
    /// Приложение само пробует восстановить адаптер: Windows иногда
    /// «залипает» и отвечает отказом в доступе к службе ленты, хотя
    /// устройство видно. Перезапуск службы Bluetooth тут не помогает --
    /// нужен перезапуск самого адаптера, а для него требуются права
    /// администратора, поэтому один раз появляется запрос.
    ///
    /// Повтор автоматического восстановления ограничен по времени: иначе
    /// окно с запросом прав появлялось бы при каждой попытке подключения.
    /// </summary>
    private void OnBleConnectFailed(string message)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _lastBleError = message;
            AppendLog($"Bluetooth: {message}");

            // Первая неудача ещё не повод перезапускать адаптер: лента могла
            // просто уйти в сон, и повторное подключение обычно срабатывает.
            //
            // Перезапуск адаптера разрывает ВСЕ Bluetooth-соединения
            // компьютера, поэтому выполняется только с разрешения в
            // настройках. По умолчанию разрешение выключено: именно частые
            // перезапуски отключали Bluetooth-пульт приставки Xiaomi от неё.
            if (!_config.AllowBluetoothRecovery)
            {
                AppendLog(
                    "Bluetooth: подключиться не удалось. Перезапуск адаптера выключен в " +
                    "настройках («Разрешать перезапуск Bluetooth»): он разрывает связь с " +
                    "другими Bluetooth-устройствами. Освободите ленту и повторите.");

                return;
            }

            if (DateTime.UtcNow - _lastBluetoothRecovery < RecoveryCooldown)
            {
                AppendLog("Bluetooth: повторное подключение не помогло, " +
                          "восстановление адаптера пропущено (недавно уже выполнялось).");
                return;
            }

            _lastBluetoothRecovery = DateTime.UtcNow;

            AppendLog("Bluetooth: пробую восстановить адаптер. " +
                      "Появится запрос прав -- нажмите «Да».");

            Task.Run(RestartBluetoothAdapter);
        }));
    }

    /// <summary>
    /// Как часто разрешено автоматически перезапускать адаптер.
    ///
    /// Срок согласован с ограничением в провайдере ленты: перезапуск рвёт
    /// связь со всеми Bluetooth-устройствами компьютера, поэтому десять
    /// минут -- разумный минимум.
    /// </summary>
    private static readonly TimeSpan RecoveryCooldown = TimeSpan.FromMinutes(10);

    private DateTime _lastBluetoothRecovery = DateTime.MinValue;

    /// <summary>
    /// Перезапускает Bluetooth-адаптер сценарием из папки программы.
    ///
    /// Сценарий сам запрашивает права администратора: без них управление
    /// устройствами невозможно. После перезапуска подключение повторяется
    /// автоматически, чтобы пользователю не пришлось ничего нажимать.
    /// </summary>
    private void RestartBluetoothAdapter()
    {
        try
        {
            string script = Path.Combine(AppContext.BaseDirectory, "ВОССТАНОВИТЬ-BLUETOOTH.ps1");

            if (!File.Exists(script))
            {
                Dispatcher.BeginInvoke(new Action(() =>
                    AppendLog("Bluetooth: не найден сценарий восстановления " +
                              "ВОССТАНОВИТЬ-BLUETOOTH.ps1")));
                return;
            }

            var start = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"",
                UseShellExecute = true,
                Verb = "runas",              // запрос прав администратора
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Minimized,
            };

            using var process = System.Diagnostics.Process.Start(start);
            process?.WaitForExit(60000);

            Dispatcher.BeginInvoke(new Action(() =>
            {
                AppendLog("Bluetooth: адаптер перезапущен, подключаюсь заново.");
                StartCapture(silent: true);
            }));
        }
        catch (Exception ex)
        {
            // Отказ в запросе прав -- не ошибка приложения: сообщаем и
            // оставляем ручной сценарий.
            Dispatcher.BeginInvoke(new Action(() =>
                AppendLog($"Bluetooth: восстановить адаптер не удалось ({ex.Message}). " +
                          "Запустите ВОССТАНОВИТЬ-BLUETOOTH.ps1 вручную.")));
        }
    }

    /// <summary>Последняя неудача подключения -- показывается в строке состояния.</summary>
    private string? _lastBleError;

    // ================================================================ запуск

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        _hotkeys = new HotkeyManager(new System.Windows.Interop.WindowInteropHelper(this).Handle);

        LoadMonitorList();
        LoadAudioDeviceList();
        LoadDeviceTypeList();
        BuildPalette();
        LoadUiFromConfig();
        ApplyHotkeys();

        // Приводим ручной цвет к тому, что показывают ползунки.
        //
        // Обработчик изменения ползунков вызывается ещё при разборе XAML, и
        // тогда он выходит сразу: элементы предпросмотра ещё не созданы. Из-за
        // этого ручной цвет оставался значением по умолчанию (чёрным), хотя
        // ползунки стояли на 255, а рамка предпросмотра была белой. Нажатие
        // «Применить цвет» отправляло чёрный.
        OnManualColorChanged(this, null!);

        AppendLog("OpenTuya Sync запущен.");
        AppendLog($"Каталог настроек: {ConfigStore.DataDirectory}");

        CheckServiceStatus();

        if (_config.AutoStartCapture)
            Dispatcher.BeginInvoke(new Action(() => StartCapture(silent: true)));
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Закрытие окна не завершает приложение: оно остаётся в трее,
        // иначе подсветка гасла бы при случайном нажатии крестика.
        e.Cancel = true;
        Hide();
        AppendLog("Окно свёрнуто в трей. Приложение продолжает работать.");
    }

    private void OnMinimizeToTray(object sender, RoutedEventArgs e)
    {
        Hide();
        AppendLog("Окно свёрнуто в трей.");
    }

    private void OnExit(object sender, RoutedEventArgs e) => Shutdown();

    /// <summary>Корректное завершение: останавливаем подсветку, отпускаем
    /// горячие клавиши и просим службу вернуть лампы в прежнее состояние.</summary>
    public void Shutdown()
    {
        try
        {
            _engine?.Stop();
            _hotkeys?.Dispose();

            if (_config.RestoreSceneOnExit)
                _service.SendCommand(new IpcMessage { Type = MessageType.DisconnectDevices }, 3000);

            _service.Dispose();
        }
        catch (Exception)
        {
            // Завершение не должно падать.
        }

        Application.Current.Shutdown();
    }

    // ============================================================ настройки UI

    private void LoadMonitorList()
    {
        _loadingUi = true;
        try
        {
            MonitorBox.Items.Clear();
            foreach (var monitor in MonitorEnumerator.Enumerate())
                MonitorBox.Items.Add(monitor);

            int index = Math.Clamp(_config.Screen.Monitor, 0, Math.Max(0, MonitorBox.Items.Count - 1));
            if (MonitorBox.Items.Count > 0) MonitorBox.SelectedIndex = index;
        }
        finally
        {
            _loadingUi = false;
        }
    }

    private void LoadAudioDeviceList()
    {
        _loadingUi = true;
        try
        {
            AudioBox.Items.Clear();
            AudioBox.Items.Add("(по умолчанию)");

            foreach (string device in MusicCaptureSource.ListOutputDevices())
                AudioBox.Items.Add(device);

            int index = 0;
            if (!string.IsNullOrWhiteSpace(_config.Music.OutputDevice))
            {
                int found = AudioBox.Items.IndexOf(_config.Music.OutputDevice);
                if (found >= 0) index = found;
            }
            AudioBox.SelectedIndex = index;
        }
        catch (Exception ex)
        {
            AppendLog($"Не удалось получить список устройств вывода: {ex.Message}");
        }
        finally
        {
            _loadingUi = false;
        }
    }

    private void LoadDeviceTypeList()
    {
        _loadingUi = true;
        try
        {
            DeviceTypeBox.Items.Clear();
            foreach (DeviceKind kind in Enum.GetValues<DeviceKind>())
                DeviceTypeBox.Items.Add(new ComboBoxItem
                {
                    Content = ProviderFactory.CaptionFor(kind),
                    Tag = kind,
                });

            TuyaVersionBox.Items.Clear();
            foreach (string version in new[] { "3.1", "3.2", "3.3", "3.4", "3.5" })
                TuyaVersionBox.Items.Add(version);
            TuyaVersionBox.SelectedIndex = 4;
        }
        finally
        {
            _loadingUi = false;
        }
    }

    private void LoadUiFromConfig()
    {
        _loadingUi = true;
        try
        {
            switch (_config.ActiveSource)
            {
                case SourceKind.Music: RadioMusic.IsChecked = true; break;
                case SourceKind.Flash: RadioFlash.IsChecked = true; break;
                default: RadioScreen.IsChecked = true; break;
            }

            MinBrightnessSlider.Value = GetBrightnessLimit(includeMin: true);
            MaxBrightnessSlider.Value = GetBrightnessLimit(includeMin: false);
            MinBrightnessText.Text = $"{MinBrightnessSlider.Value:0}%";
            MaxBrightnessText.Text = $"{MaxBrightnessSlider.Value:0}%";

            AutoStartBox.IsChecked = _config.AutoStart;
            StartMinimizedBox.IsChecked = _config.StartMinimized;
            AutoStartCaptureBox.IsChecked = _config.AutoStartCapture;
            RestoreSceneBox.IsChecked = _config.RestoreSceneOnExit;
            AllowBluetoothRecoveryBox.IsChecked = _config.AllowBluetoothRecovery;

            RefreshHotkeyBoxes();
            ReloadDeviceList();
        }
        finally
        {
            _loadingUi = false;
        }
    }

    private int GetBrightnessLimit(bool includeMin) => _config.ActiveSource switch
    {
        SourceKind.Music => includeMin ? _config.Music.BrightnessLimitMin : _config.Music.BrightnessLimitMax,
        SourceKind.Flash => includeMin ? _config.Flash.BrightnessLimitMin : _config.Flash.BrightnessLimitMax,
        _ => includeMin ? _config.Screen.BrightnessLimitMin : _config.Screen.BrightnessLimitMax,
    };

    private void SetBrightnessLimit(bool includeMin, int value)
    {
        switch (_config.ActiveSource)
        {
            case SourceKind.Music:
                if (includeMin) _config.Music.BrightnessLimitMin = value; else _config.Music.BrightnessLimitMax = value;
                break;
            case SourceKind.Flash:
                if (includeMin) _config.Flash.BrightnessLimitMin = value; else _config.Flash.BrightnessLimitMax = value;
                break;
            default:
                if (includeMin) _config.Screen.BrightnessLimitMin = value; else _config.Screen.BrightnessLimitMax = value;
                break;
        }
    }

    private int CurrentIntervalMs => _config.ActiveSource switch
    {
        SourceKind.Music => _config.Music.SendIntervalMs,
        SourceKind.Flash => 150,
        _ => _config.Screen.IntervalMs,
    };

    // ============================================================ вкладка «Автоматически»

    private void OnSourceChanged(object sender, RoutedEventArgs e)
    {
        // Событие Checked приходит ещё при разборе XAML: в разметке у
        // первой радиокнопки стоит IsChecked="True", и обработчик
        // вызывается раньше, чем созданы остальные элементы. Без этой
        // проверки приложение падало с NullReferenceException прямо при
        // запуске.
        if (_loadingUi || _config == null || MonitorRow == null || AudioRow == null) return;

        _config.ActiveSource = RadioMusic.IsChecked == true ? SourceKind.Music
            : RadioFlash.IsChecked == true ? SourceKind.Flash
            : SourceKind.Screen;

        MonitorRow.Visibility = _config.ActiveSource == SourceKind.Screen
            ? Visibility.Visible : Visibility.Collapsed;
        AudioRow.Visibility = _config.ActiveSource == SourceKind.Music
            ? Visibility.Visible : Visibility.Collapsed;

        _loadingUi = true;
        try
        {
            MinBrightnessSlider.Value = GetBrightnessLimit(includeMin: true);
            MaxBrightnessSlider.Value = GetBrightnessLimit(includeMin: false);
        }
        finally
        {
            _loadingUi = false;
        }

        MinBrightnessText.Text = $"{MinBrightnessSlider.Value:0}%";
        MaxBrightnessText.Text = $"{MaxBrightnessSlider.Value:0}%";

        // Смена источника на ходу требует перезапуска захвата.
        if (_engine?.IsRunning == true)
        {
            AppendLog("Источник изменён -- перезапускаю подсветку.");
            StartCapture();
        }
    }

    private void OnMonitorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi || _config == null) return;
        if (MonitorBox.SelectedItem is MonitorInfo monitor)
        {
            _config.Screen.Monitor = monitor.Index;
            SaveConfig();
        }
    }

    private void OnAudioDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi || _config == null) return;
        _config.Music.OutputDevice = AudioBox.SelectedIndex <= 0
            ? ""
            : AudioBox.SelectedItem?.ToString() ?? "";
        SaveConfig();
    }

    private void OnMinBrightnessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MinBrightnessText == null) return;

        MinBrightnessText.Text = $"{e.NewValue:0}%";

        // Минимум не может быть выше максимума: тянем максимум за собой.
        if (e.NewValue > MaxBrightnessSlider.Value)
            MaxBrightnessSlider.Value = e.NewValue;

        if (_loadingUi) return;
        SetBrightnessLimit(includeMin: true, (int)e.NewValue);
        ApplyBrightnessLimitsLive();
        SaveConfig();
    }

    private void OnMaxBrightnessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MaxBrightnessText == null) return;

        MaxBrightnessText.Text = $"{e.NewValue:0}%";

        if (e.NewValue < MinBrightnessSlider.Value)
            MinBrightnessSlider.Value = e.NewValue;

        if (_loadingUi) return;
        SetBrightnessLimit(includeMin: false, (int)e.NewValue);
        ApplyBrightnessLimitsLive();
        SaveConfig();
    }

    /// <summary>
    /// Меняет пределы яркости на ходу, не перезапуская захват: это и
    /// позволяет сбить всплеск вроде флешки в игре на лету.
    /// </summary>
    private void ApplyBrightnessLimitsLive()
    {
        _engine?.SetBrightnessLimits(
            (int)MinBrightnessSlider.Value,
            (int)MaxBrightnessSlider.Value);
    }

    private void OnStartStop(object sender, RoutedEventArgs e)
    {
        if (_engine?.IsRunning == true) StopCapture();
        else StartCapture();
    }

    private void OnStartCapture(object sender, RoutedEventArgs e) => StartCapture();

    private void OnStopCapture(object sender, RoutedEventArgs e) => StopCapture();

    private void StartCapture(bool silent = false)
    {
        try
        {
            StopCapture(silent: true);

            var response = _service.SendCommand(new IpcMessage { Type = MessageType.ConnectDevices });

            // Ошибка службы не отменяет запуск: Bluetooth-ленты
            // обслуживаются приложением напрямую, и подсветка для них
            // будет работать даже без службы.
            if (response.Ok != true)
                AppendLog($"Служба: {response.Text}");
            else
                AppendLog(response.Text ?? "Устройства подключены.");

            if (!_service.IsConnected && !HasBleDevices())
            {
                if (!silent)
                    MessageBox.Show(this,
                        response.Text ?? "Нет ни службы, ни подключённых Bluetooth-устройств.",
                        "OpenTuya Sync", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Захват запускается сразу, не дожидаясь подключения ленты:
            // установление связи с BLE-устройством занимает секунды, и
            // интерфейс не должен на это время замирать. Номер намерения
            // защищает от гонки: если пользователь успеет переключить
            // источник, устаревший результат не будет применён.
            StartEngine();

            int generation = _localBle.ConnectAsyncGeneration;
            AppendLog("Подключение Bluetooth-устройств...");

            _ = Task.Run(() => _localBle.Connect(_config))
                .ContinueWith(task => OnBleConnected(task, generation),
                    TaskScheduler.FromCurrentSynchronizationContext());
        }
        catch (Exception ex)
        {
            AppendLog($"Ошибка запуска: {ex.Message}");
            if (!silent)
                MessageBox.Show(this, ex.Message, "Не удалось запустить подсветку",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            StopCapture(silent: true);
        }
    }

    /// <summary>Создаёт и запускает движок подсветки.</summary>
    private void StartEngine()
    {
        _currentSource = SourceFactory.Create(_config.ActiveSource, _config);

        _engine = new CaptureEngine(OnColorReady);
        _engine.Start(
            _currentSource,
            CurrentIntervalMs,
            (int)MinBrightnessSlider.Value,
            (int)MaxBrightnessSlider.Value);

        StartStopButton.Content = "Остановить";

        if (_currentSource is ScreenCaptureSource screen)
            AppendLog($"Захват экрана запущен (монитор {_config.Screen.Monitor}).");

        AppendLog($"Подсветка включена: {SourceFactory.CaptionFor(_config.ActiveSource)}");
    }

    private bool HasBleDevices() =>
        _config.Devices.Any(d => d.Enabled && d.Type == DeviceKind.ElkBledob);

    private void StopCapture(bool silent = false)
    {
        try
        {
            if (_engine != null)
            {
                _engine.Stop();
                _engine.Dispose();
                _engine = null;
            }

            _currentSource?.Dispose();
            _currentSource = null;

            // Bluetooth-ленты обслуживает приложение: при остановке
            // подсветки их тоже нужно отпустить, иначе устройство остаётся
            // занятым и повторное подключение не пройдёт.
            _localBle.DisconnectAll();

            StartStopButton.Content = "Запустить";

            if (_config.RestoreSceneOnExit)
                _service.SendCommand(new IpcMessage { Type = MessageType.DisconnectDevices }, 3000);

            if (!silent) AppendLog("Подсветка остановлена.");
        }
        catch (Exception ex)
        {
            AppendLog($"Ошибка остановки: {ex.Message}");
        }
    }

    /// <summary>
    /// Вызывается по завершении подключения Bluetooth-устройств. Результат
    /// применяется, только если за это время пользователь не переключил
    /// источник: иначе подключение уже не нужно.
    /// </summary>
    private void OnBleConnected(Task<List<string>> task, int generation)
    {
        if (task.IsFaulted)
        {
            AppendLog($"Bluetooth: ошибка подключения: {task.Exception?.GetBaseException().Message}");
            return;
        }

        if (_localBle.Generation != generation)
        {
            // Источник переключили, пока шло подключение -- результат
            // устарел. Устройства уже снимаются фоновой задачей.
            AppendLog("Bluetooth: подключение отменено (источник переключён).");
            return;
        }

        var errors = task.Result;

        if (errors.Count == 0)
        {
            _lastBleError = null;
            AppendLog(_localBle.StatusText);
        }
        else
        {
            // Ошибку запоминаем: она показывается в строке состояния, пока
            // связь не восстановится.
            _lastBleError = string.Join("; ", errors);

            foreach (string error in errors)
                AppendLog($"Bluetooth: {error}");
        }
    }

    /// <summary>Статус Bluetooth-устройств для строки состояния.</summary>
    public string BleStatusText => _localBle.StatusText;

    /// <summary>Получатель цвета от движка: переправляет его службе.</summary>
    /// <summary>
    /// Получатель цвета от движка: переправляет его службе (проводные
    /// устройства) и напрямую Bluetooth-лентам.
    /// </summary>
    private void OnColorReady(Rgb color)
    {
        _service.SendColor(color);
        _localBle.ApplyColor(color);
    }

    private void OnReconnectDevices(object sender, RoutedEventArgs e)
    {
        // Подключение к устройствам идёт в фоне: у Bluetooth-ленты оно
        // занимает до тридцати секунд, и в потоке интерфейса окно на это
        // время замирает.
        if (_reconnecting)
        {
            AppendLog("Переподключение уже идёт.");
            return;
        }

        _reconnecting = true;
        AppendLog("Переподключение устройств...");

        // Ручное действие снимает паузу после неудач: пользователь мог
        // устранить причину (освободить ленту) и не должен ждать.
        if (_localBle.IsPaused)
        {
            AppendLog("Пауза после неудачных попыток снята: переподключение запущено вручную.");
            _localBle.ResetFailurePause();
        }

        var response = _service.SendCommand(new IpcMessage { Type = MessageType.ConnectDevices });
        AppendLog(response.Text ?? "Готово.");

        int generation = _localBle.ConnectAsyncGeneration;

        Task.Run(() => _localBle.Connect(_config))
            .ContinueWith(task =>
            {
                _reconnecting = false;

                if (task.IsFaulted)
                {
                    AppendLog($"Переподключение не удалось: " +
                              $"{task.Exception?.GetBaseException().Message}");
                    return;
                }

                if (_localBle.Generation != generation)
                {
                    AppendLog("Переподключение отменено: состояние изменилось.");
                    return;
                }

                if (task.Result.Count == 0)
                    AppendLog($"Переподключено. {_localBle.StatusText}");
                else
                    foreach (string error in task.Result)
                        AppendLog($"Bluetooth: {error}");
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private bool _reconnecting;

    /// <summary>
    /// Палитра базовых цветов для ручного управления.
    ///
    /// Набор подобран по кругу оттенков: сначала яркие цвета, затем тёмные и
    /// пастельные, в конце нейтральные. Так нужный цвет находится за один
    /// взгляд, без подбора ползунками.
    /// </summary>
    private static readonly (string Name, byte R, byte G, byte B)[] PaletteColors =
    {
        // Тёплые
        ("Красный",          255,   0,   0),
        ("Алый",             255,  64,   0),
        ("Оранжевый",        255, 128,   0),
        ("Янтарный",         255, 176,   0),
        ("Жёлтый",           255, 224,   0),
        ("Лайм",             192, 255,   0),

        // Холодные
        ("Зелёный",            0, 255,   0),
        ("Изумрудный",         0, 230, 118),
        ("Бирюзовый",          0, 255, 200),
        ("Голубой",            0, 200, 255),
        ("Синий",              0,   0, 255),
        ("Индиго",            64,   0, 255),

        // Смешанные
        ("Фиолетовый",       160,   0, 255),
        ("Пурпурный",        255,   0, 200),
        ("Розовый",          255,  64, 160),
        ("Малиновый",        220,   0,  80),

        // Тёмные
        ("Тёмно-красный",    128,   0,   0),
        ("Тёмно-зелёный",      0, 110,   0),
        ("Тёмно-синий",        0,   0, 128),
        ("Болотный",          96, 110,   0),

        // Пастельные
        ("Персиковый",       255, 200, 150),
        ("Мятный",           180, 255, 220),
        ("Небесный",         170, 210, 255),
        ("Лавандовый",       200, 180, 255),

        // Нейтральные
        ("Белый",            255, 255, 255),
        ("Тёплый белый",     255, 240, 210),
        ("Холодный белый",   210, 240, 255),
        ("Серый",            128, 128, 128),
    };

    /// <summary>
    /// Строит палитру кнопками. Кнопка, а не рамка: она сама показывает
    /// наведение и нажатие, и по ней видно, что цвет можно нажать.
    /// </summary>
    private void BuildPalette()
    {
        var panel = new WrapPanel();

        foreach (var (name, r, g, b) in PaletteColors)
        {
            var button = new Button
            {
                Width = 34,
                Height = 34,
                Margin = new Thickness(0, 0, 4, 4),
                Padding = new Thickness(0),

                // Светлый фон кнопки заменяется цветом: кнопка и есть образец.
                Background = new SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(r, g, b)),
                BorderBrush = new SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88)),
                BorderThickness = new Thickness(1),
                ToolTip = $"{name}  ·  {r},{g},{b}",
                Tag = new Rgb(r / 255f, g / 255f, b / 255f),
            };

            button.Click += OnPaletteColorClick;
            panel.Children.Add(button);
        }

        PalettePanel.Items.Add(panel);
    }

    /// <summary>
    /// Нажатие цвета в палитре: останавливает автоматику и сразу отправляет
    /// цвет. Так же работают остальные ручные действия -- иначе движок
    /// перебивал бы выбранный цвет несколько раз в секунду.
    /// </summary>
    private void OnPaletteColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Rgb color }) return;

        StopAutoForManualAction();

        _manualColor = color;

        // Ползунки и предпросмотр приводим к выбранному цвету, чтобы
        // интерфейс показывал именно то, что отправлено.
        _suppressManualSend = true;
        try
        {
            var (r, g, b) = color.ToBytes();
            RedSlider.Value = r;
            GreenSlider.Value = g;
            BlueSlider.Value = b;
        }
        finally
        {
            _suppressManualSend = false;
        }

        OnManualColorChanged(this, null!);

        _service.SendColor(color);
        _localBle.ApplyColor(color);

        AppendLog($"Палитра: отправлен цвет {color}");
    }

    /// <summary>
    /// Останавливает автоматическую подсветку, если она работает.
    ///
    /// Нужно перед ручными действиями: иначе движок перебивает выбранный
    /// цвет несколько раз в секунду, и кажется, что ничего не происходит.
    /// Возвращает true, если подсветка была остановлена.
    /// </summary>
    private bool StopAutoForManualAction()
    {
        if (_engine?.IsRunning != true) return false;

        AppendLog("Автоматическая подсветка остановлена: начато ручное управление.");
        StopCapture(silent: true);
        return true;
    }

    // ============================================================ вкладка «Вручную»

    private void OnManualColorChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Как и у радиокнопок, событие приходит при разборе XAML: у
        // ползунков заданы начальные значения, и обработчик вызывается
        // раньше, чем созданы элементы, к которым он обращается.
        if (RedText == null || GreenText == null || BlueText == null || ColorPreview == null) return;

        RedText.Text = $"{RedSlider.Value:0}";
        GreenText.Text = $"{GreenSlider.Value:0}";
        BlueText.Text = $"{BlueSlider.Value:0}";

        _manualColor = new Rgb(
            (float)(RedSlider.Value / 255.0),
            (float)(GreenSlider.Value / 255.0),
            (float)(BlueSlider.Value / 255.0));

        ColorPreview.Background = new SolidColorBrush(
            System.Windows.Media.Color.FromRgb(
                (byte)RedSlider.Value, (byte)GreenSlider.Value, (byte)BlueSlider.Value));

        // Ползунки, изменённые из палитры, уже отправили цвет: повторять
        // отправку не нужно.
        if (_suppressManualSend) return;

        if (_engine?.IsRunning == true)
        {
            // Во время автоматической подсветки ползунки только показывают
            // цвет: отправлять его сейчас бессмысленно, движок перебьёт.
            return;
        }
    }

    private void OnApplyManualColor(object sender, RoutedEventArgs e)
    {
        // Ручное действие само останавливает автоматическую подсветку.
        // Раньше здесь показывалось окно с требованием остановить её
        // вручную, и складывалось впечатление, что программа зависла.
        StopAutoForManualAction();

        _service.SendColor(_manualColor);

        // Если лента ещё не подключена, команда не теряется: подключение
        // запустится в фоне и цвет уйдёт сразу после него.
        bool delivered = _localBle.ApplyColorOrConnect(_config, _manualColor);

        AppendLog(delivered
            ? $"Отправлен цвет: {_manualColor}"
            : $"Отправлен цвет: {_manualColor} (лента подключается, цвет уйдёт после подключения)");
    }

    private void OnManualPowerOn(object sender, RoutedEventArgs e)
    {
        StopAutoForManualAction();

        _service.SendCommand(new IpcMessage { Type = MessageType.SetDevicePower, On = true }, 3000);

        bool delivered = _localBle.TurnOnOrConnect(_config);
        AppendLog(delivered ? "Лента включена." : "Лента включается...");
    }

    private void OnManualPowerOff(object sender, RoutedEventArgs e)
    {
        StopAutoForManualAction();

        _service.SendCommand(new IpcMessage { Type = MessageType.SetDevicePower, On = false }, 3000);

        bool delivered = _localBle.TurnOffOrConnect(_config);
        AppendLog(delivered ? "Лента выключена." : "Лента выключается...");
    }

    private void OnPickColor(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(
                (int)RedSlider.Value, (int)GreenSlider.Value, (int)BlueSlider.Value),
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        // Ползунки получают новые значения; обработчик изменения сам
        // обновит предпросмотр и запомнит выбранный цвет.
        RedSlider.Value = dialog.Color.R;
        GreenSlider.Value = dialog.Color.G;
        BlueSlider.Value = dialog.Color.B;

        OnManualColorChanged(this, null!);
    }

    // ============================================================ устройства

    private void ReloadDeviceList()
    {
        DeviceList.Items.Clear();

        foreach (var device in _config.Devices)
        {
            DeviceList.Items.Add(new ListBoxItem
            {
                Content = $"{(device.Enabled ? "✔" : "✘")}  {device.Name}   [{device.TypeCaption}]",
                Tag = device,
            });
        }

        if (DeviceList.Items.Count > 0 && _selectedDevice == null)
            DeviceList.SelectedIndex = 0;
    }

    private void OnDeviceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceList.SelectedItem is not ListBoxItem item) return;

        _selectedDevice = item.Tag as DeviceConfig;
        LoadDeviceIntoEditor();
    }

    private void LoadDeviceIntoEditor()
    {
        _loadingUi = true;
        try
        {
            var device = _selectedDevice;
            if (device == null)
            {
                DeviceNameBox.Text = "";
                return;
            }

            DeviceNameBox.Text = device.Name;

            foreach (ComboBoxItem item in DeviceTypeBox.Items)
                if (item.Tag is DeviceKind kind && kind == device.Type)
                {
                    DeviceTypeBox.SelectedItem = item;
                    break;
                }

            TuyaDevIdBox.Text = device.Tuya?.DevId ?? "";
            TuyaKeyBox.Text = device.Tuya?.LocalKey ?? "";
            TuyaVersionBox.SelectedItem = (device.Tuya?.Version ?? 3.5).ToString("0.0",
                System.Globalization.CultureInfo.InvariantCulture);

            IpBox.Text = device.Type switch
            {
                DeviceKind.Tuya => device.Tuya?.Ip ?? "",
                DeviceKind.Wled => device.Wled?.Ip ?? "",
                DeviceKind.Hue => device.Hue?.BridgeIp ?? "",
                DeviceKind.ElkBledob => device.ElkBledob?.Mac ?? "",
                _ => "",
            };

            DeviceEnabledBox.IsChecked = device.Enabled;
            DeviceBrightnessSlider.Value = Math.Clamp(device.BrightnessScale * 100, 10, 200);
        }
        finally
        {
            _loadingUi = false;
        }
    }

    private void OnDeviceTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi) return;

        bool isTuya = CurrentEditorType() == DeviceKind.Tuya;
        bool isElk = CurrentEditorType() == DeviceKind.ElkBledob;
        bool isHue = CurrentEditorType() == DeviceKind.Hue;
        bool isWled = CurrentEditorType() == DeviceKind.Wled;

        TuyaDevIdBox.IsEnabled = isTuya;
        TuyaKeyBox.IsEnabled = isTuya;
        TuyaVersionBox.IsEnabled = isTuya;

        // Поле IP переиспользуется под все типы, но подписывается по-разному.
        if (isElk) IpBox.Text = _selectedDevice?.ElkBledob?.Mac ?? IpBox.Text;
    }

    private DeviceKind CurrentEditorType() =>
        DeviceTypeBox.SelectedItem is ComboBoxItem item && item.Tag is DeviceKind kind
            ? kind
            : DeviceKind.Tuya;

    private void OnDeviceBrightnessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DeviceBrightnessText == null) return;
        DeviceBrightnessText.Text = $"{e.NewValue:0}%";
    }

    private void OnAddDevice(object sender, RoutedEventArgs e)
    {
        var device = new DeviceConfig
        {
            Name = $"Устройство {_config.Devices.Count + 1}",
            Type = DeviceKind.Tuya,
            Enabled = true,
            Tuya = new TuyaSettings(),
        };

        _config.Devices.Add(device);
        _selectedDevice = device;
        SaveConfig();
        ReloadDeviceList();
        LoadDeviceIntoEditor();
        AppendLog($"Добавлено устройство: {device.Name}");
    }

    private void OnEditDevice(object sender, RoutedEventArgs e)
    {
        if (_selectedDevice == null)
        {
            MessageBox.Show(this, "Сначала выберите устройство в списке.",
                "Устройства", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        LoadDeviceIntoEditor();
    }

    private void OnDeleteDevice(object sender, RoutedEventArgs e)
    {
        if (_selectedDevice == null) return;

        var answer = MessageBox.Show(this,
            $"Удалить устройство «{_selectedDevice.Name}»?",
            "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes) return;

        _config.Devices.Remove(_selectedDevice);
        _selectedDevice = null;
        SaveConfig();
        ReloadDeviceList();
        AppendLog("Устройство удалено.");
    }

    /// <summary>Переносит данные из полей редактора в выбранное устройство.</summary>
    private bool CommitDeviceEditor()
    {
        if (_selectedDevice == null)
        {
            MessageBox.Show(this, "Устройство не выбрано.", "Устройства",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        string name = DeviceNameBox.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show(this, "Введите название устройства.", "Устройства",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var device = _selectedDevice;
        device.Name = name;
        device.Type = CurrentEditorType();
        device.Enabled = DeviceEnabledBox.IsChecked == true;
        device.BrightnessScale = (float)(DeviceBrightnessSlider.Value / 100.0);

        string ip = IpBox.Text.Trim();

        switch (device.Type)
        {
            case DeviceKind.Tuya:
                string devId = TuyaDevIdBox.Text.Trim();
                string key = TuyaKeyBox.Text.Trim();

                if (devId.Length == 0 || ip.Length == 0 || key.Length == 0)
                {
                    MessageBox.Show(this,
                        "Для Tuya обязательны Device ID, IP и Local Key.",
                        "Не хватает данных", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                double version = 3.5;
                if (TuyaVersionBox.SelectedItem is string versionText)
                    double.TryParse(versionText, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out version);

                device.Tuya = new TuyaSettings
                {
                    DevId = devId, Ip = ip, LocalKey = key, Version = version,
                };
                break;

            case DeviceKind.Wled:
                if (ip.Length == 0)
                {
                    MessageBox.Show(this, "Для WLED нужен IP-адрес.", "Не хватает данных",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
                device.Wled = new WledSettings { Ip = ip };
                break;

            case DeviceKind.Hue:
                if (ip.Length == 0 || device.Hue == null || device.Hue.Username.Length == 0)
                {
                    MessageBox.Show(this,
                        "Для Hue нужны IP моста и сопряжение (кнопка «Сопряжение с Hue»).",
                        "Не хватает данных", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
                device.Hue.BridgeIp = ip;
                break;

            case DeviceKind.ElkBledob:
                if (ip.Length == 0)
                {
                    MessageBox.Show(this, "Для Bluetooth-ленты нужен MAC-адрес.",
                        "Не хватает данных", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
                device.ElkBledob = new ElkSettings { Mac = ip };
                break;
        }

        return true;
    }

    private void OnSaveDevice(object sender, RoutedEventArgs e)
    {
        if (!CommitDeviceEditor()) return;

        SaveConfig();

        // Значение масштаба яркости пишется в журнал: при проверке
        // сообщалось, что оно не сохраняется, и без записи непонятно, теряется
        // оно до записи в файл или после.
        AppendLog($"Устройство сохранено: {_selectedDevice?.Name}, " +
                  $"масштаб яркости {(_selectedDevice?.BrightnessScale ?? 0) * 100:0}%");

        ReloadDeviceList();

        // Устройства на службе пересобираются, чтобы применились новые
        // параметры подключения.
        _service.SendCommand(new IpcMessage { Type = MessageType.ReloadConfig }, 3000);
    }

    private void OnTestDevice(object sender, RoutedEventArgs e)
    {
        if (_selectedDevice == null) return;

        if (!CommitDeviceEditor()) return;
        SaveConfig();

        // Bluetooth-ленту проверяет приложение: служба работает в сессии 0,
        // где Bluetooth недоступен, и проверка через неё всегда упиралась в
        // ожидание и таймаут.
        if (_selectedDevice.Type == DeviceKind.ElkBledob)
        {
            TestBleDevice(_selectedDevice);
            return;
        }

        _service.SendCommand(new IpcMessage { Type = MessageType.ReloadConfig }, 3000);

        AppendLog($"Проверяю связь: {_selectedDevice.Name}...");
        var response = _service.SendCommand(new IpcMessage
        {
            Type = MessageType.TestDevice,
            DeviceId = _selectedDevice.Id,
        }, 20000);

        AppendLog(response.Text ?? "Нет ответа.");
        MessageBox.Show(this, response.Text ?? "Нет ответа.", "Проверка связи",
            MessageBoxButton.OK,
            response.Ok == true ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    /// <summary>
    /// Проверка связи с Bluetooth-лентой: подключение и отправка цвета.
    /// Выполняется в фоне, потому что установление связи занимает время.
    /// </summary>
    private void TestBleDevice(DeviceConfig device)
    {
        AppendLog($"Проверяю связь с лентой: {device.Name}...");

        Task.Run(() =>
        {
            string? error = _localBle.Reconnect(_config, device.Id);
            if (error == null)
            {
                // Отправляем заметный цвет, чтобы результат был виден глазами.
                _localBle.ApplyColor(new Rgb(1f, 0.5f, 0f));
                Thread.Sleep(600);
            }

            return error;
        }).ContinueWith(task =>
        {
            string? error = task.IsFaulted
                ? task.Exception?.GetBaseException().Message
                : task.Result;

            string text = error == null
                ? $"Связь есть: {device.Name} ({_localBle.StatusText}). Отправлен оранжевый цвет."
                : $"{device.Name}: {error}";

            AppendLog(text);
            MessageBox.Show(this, text, "Проверка связи", MessageBoxButton.OK,
                error == null ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ============================================================ поиск устройств

    private void OnScanTuya(object sender, RoutedEventArgs e) =>
        RunScan(MessageType.ScanTuya, "Идёт поиск в локальной сети... (10-20 секунд)");

    private void OnScanBluetooth(object sender, RoutedEventArgs e) =>
        RunScan(MessageType.ScanBluetooth, "Идёт поиск по Bluetooth... (около 8 секунд)");

    private void RunScan(MessageType type, string statusText)
    {
        ScanStatus.Text = statusText;
        ScanResultsBox.Visibility = Visibility.Collapsed;
        ScanResultsBox.Items.Clear();
        CancelScanButton.IsEnabled = true;

        _scanCts = new CancellationTokenSource();

        Task.Run(() =>
        {
            var response = _service.SendCommand(new IpcMessage { Type = type }, 40000);
            return response;
        }).ContinueWith(task =>
        {
            CancelScanButton.IsEnabled = false;

            if (task.IsFaulted)
            {
                ScanStatus.Text = $"Ошибка поиска: {task.Exception?.GetBaseException().Message}";
                return;
            }

            var response = task.Result;
            var found = response.Discovered ?? new List<DiscoveredDevice>();

            ScanStatus.Text = response.Text ?? $"Найдено: {found.Count}";

            if (found.Count == 0) return;

            foreach (var device in found)
                ScanResultsBox.Items.Add(device);

            ScanResultsBox.Visibility = Visibility.Visible;
            AppendLog($"Поиск завершён: найдено {found.Count}.");
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void OnCancelScan(object sender, RoutedEventArgs e)
    {
        _scanCts?.Cancel();
        CancelScanButton.IsEnabled = false;
        ScanStatus.Text = "Поиск отменён.";
    }

    /// <summary>Переносит найденное устройство в редактор.</summary>
    private void OnScanResultSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ScanResultsBox.SelectedItem is not DiscoveredDevice found) return;

        _lastScanResult = found;

        if (found.IsBluetooth)
        {
            // Bluetooth-лента: тип и MAC.
            SelectDeviceType(DeviceKind.ElkBledob);
            IpBox.Text = found.Address;
        }
        else
        {
            // Tuya: Device ID, IP и версия. Local Key остаётся пустым --
            // его из сети получить нельзя.
            SelectDeviceType(DeviceKind.Tuya);
            TuyaDevIdBox.Text = found.Id;
            IpBox.Text = found.Address;
            TuyaVersionBox.SelectedItem = found.Version.ToString("0.0",
                System.Globalization.CultureInfo.InvariantCulture);

            if (string.IsNullOrWhiteSpace(TuyaVersionBox.SelectedItem as string))
                TuyaVersionBox.SelectedIndex = 4;   // 3.5 по умолчанию
        }

        AppendLog($"Из поиска подставлено: {found.Caption}");
    }

    private void SelectDeviceType(DeviceKind kind)
    {
        foreach (ComboBoxItem item in DeviceTypeBox.Items)
            if (item.Tag is DeviceKind candidate && candidate == kind)
            {
                DeviceTypeBox.SelectedItem = item;
                return;
            }
    }

    // ============================================================ Hue

    private void OnPairHue(object sender, RoutedEventArgs e)
    {
        string ip = IpBox.Text.Trim();
        if (ip.Length == 0)
        {
            MessageBox.Show(this, "Сначала введите IP моста Hue в поле IP.",
                "Сопряжение Hue", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        MessageBox.Show(this,
            "Нажмите физическую кнопку на мосте Hue, затем закройте это окно.\n" +
            "Сопряжение нужно успеть выполнить в течение 30 секунд.",
            "Сопряжение Hue", MessageBoxButton.OK, MessageBoxImage.Information);

        var response = _service.SendCommand(new IpcMessage
        {
            Type = MessageType.PairHue,
            Text = ip,
        }, 15000);

        if (response.Ok == true && !string.IsNullOrEmpty(response.Username))
        {
            _selectedDevice ??= new DeviceConfig { Name = "Hue" };
            _selectedDevice.Type = DeviceKind.Hue;
            _selectedDevice.Hue ??= new HueSettings();
            _selectedDevice.Hue.BridgeIp = ip;
            _selectedDevice.Hue.Username = response.Username;

            SaveConfig();
            AppendLog("Сопряжение с Hue выполнено, ключ сохранён.");
            MessageBox.Show(this, "Сопряжение выполнено. Ключ сохранён.",
                "Hue", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            AppendLog($"Сопряжение не удалось: {response.Text}");
            MessageBox.Show(this, response.Text ?? "Сопряжение не удалось.",
                "Hue", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ============================================================ служба

    private void OnInstallService(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "Установка службы требует прав администратора и вызовет запрос UAC.\n\n" +
            "Продолжить?",
            "Установка службы", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes) return;

        RunServiceCommand("install", elevated: true);
    }

    private void OnUninstallService(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "Удалить службу OpenTuya Sync? Подсветка перестанет работать без окна приложения.",
            "Удаление службы", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes) return;

        RunServiceCommand("uninstall", elevated: true);
    }

    private void OnCheckService(object sender, RoutedEventArgs e) => CheckServiceStatus(showDialog: true);

    /// <summary>
    /// Запускает тот же исполняемый файл в режиме установки службы. Нужны
    /// права администратора, поэтому запрос UAC обязателен.
    /// </summary>
    private void RunServiceCommand(string command, bool elevated)
    {
        string? serviceExe = FindServiceExecutable();
        if (serviceExe == null)
        {
            MessageBox.Show(this,
                "Не найден OpenTuyaSync.Service.exe рядом с приложением.\n" +
                "Соберите решение целиком (dotnet build) и повторите.",
                "Служба", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo(serviceExe, command)
            {
                UseShellExecute = true,
                Verb = elevated ? "runas" : "",
            };

            using var process = System.Diagnostics.Process.Start(startInfo);
            process?.WaitForExit(30000);

            AppendLog($"Команда службы «{command}» выполнена (код {process?.ExitCode}).");
            CheckServiceStatus();
        }
        catch (Exception ex)
        {
            AppendLog($"Не удалось выполнить команду службы: {ex.Message}");
            MessageBox.Show(this,
                $"{ex.Message}\n\nЕсли запрос прав администратора был отклонён, " +
                "установите службу вручную из командной строки, запущенной от администратора.",
                "Служба", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string? FindServiceExecutable()
    {
        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        {
            Path.Combine(baseDir, "OpenTuyaSync.Service.exe"),
            Path.Combine(baseDir, "service", "OpenTuyaSync.Service.exe"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "OpenTuyaSync.Service", "OpenTuyaSync.Service.exe")),
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private void CheckServiceStatus(bool showDialog = false)
    {
        var response = _service.SendCommand(new IpcMessage { Type = MessageType.Ping }, 2500);

        bool ok = response.Ok == true;

        StatusService.Text = ok ? "Служба: подключена" : "Служба: не запущена";

        int serviceDevices = response.Devices?.Count(d => d.Connected) ?? 0;
        int bleDevices = _localBle.ConnectedCount;

        // Счётчики разделены намеренно: Bluetooth-ленты обслуживает
        // приложение, а не служба (служба работает в сеансе 0, где
        // Bluetooth недоступен). Общий счётчик без пояснения выглядел как
        // ошибка: в разделе службы стоял ноль, хотя лента работала.
        ServiceStatusText.Text = ok
            ? $"Служба отвечает. {response.Text}\n" +
              $"Сетевых устройств на службе: {serviceDevices}\n" +
              $"Bluetooth-устройств в приложении: {bleDevices}"
            : "Служба не отвечает. Установите её кнопкой «Установить службу» " +
              "или запустите OpenTuyaSync.Service.exe --console для диагностики.";

        if (ok && response.Devices != null)
        {
            var text = new StringBuilder("Устройства на службе (сетевые):\n");

            if (response.Devices.Count == 0)
                text.AppendLine("  нет: Bluetooth-ленты обслуживает приложение, а не служба");
            else
                foreach (var device in response.Devices)
                    text.AppendLine($"  {(device.Connected ? "✔" : "✘")} {device.Name} — {device.Status}");

            // Сообщение выводится только при изменении состава устройств.
            // Иначе оно повторялось при каждой проверке состояния -- каждые
            // несколько секунд, -- и журнал превращался в одну и ту же строку.
            string summary = text.ToString().TrimEnd();

            if (summary != _lastServiceDevices)
            {
                _lastServiceDevices = summary;
                AppendLog(summary);
            }
        }

        if (showDialog)
        {
            string summary = ok
                ? $"{response.Text}\n\n" +
                  $"Сетевых устройств на службе: {serviceDevices}\n" +
                  $"Bluetooth-устройств в приложении: {bleDevices}\n\n" +
                  "Bluetooth-ленты обслуживает приложение: служба Windows работает " +
                  "в изолированном сеансе, где Bluetooth недоступен."
                : "Служба не отвечает.";

            MessageBox.Show(this, summary, "Состояние службы", MessageBoxButton.OK,
                ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        UpdateControlAvailability(ok);
    }

    /// <summary>Без службы запускать подсветку бессмысленно -- предупреждаем
    /// это в интерфейсе, а не молчаливым отсутствием реакции.</summary>
    private void UpdateControlAvailability(bool serviceAvailable)
    {
        StartStopButton.IsEnabled = serviceAvailable || _engine?.IsRunning == true;
    }

    // ============================================================ горячие клавиши

    private void RefreshHotkeyBoxes()
    {
        HotkeyToggleBox.Text = FormatHotkey(_config.Hotkeys.StartStopKey, _config.Hotkeys.StartStopModifiers);
        HotkeyNextSourceBox.Text = FormatHotkey(_config.Hotkeys.NextSourceKey, _config.Hotkeys.NextSourceModifiers);
        HotkeyBrightnessUpBox.Text = FormatHotkey(_config.Hotkeys.BrightnessUpKey, _config.Hotkeys.BrightnessUpModifiers);
        HotkeyBrightnessDownBox.Text = FormatHotkey(_config.Hotkeys.BrightnessDownKey, _config.Hotkeys.BrightnessDownModifiers);
    }

    private static string FormatHotkey(int virtualKey, int modifiers)
    {
        if (virtualKey == 0) return "(не задано)";

        var parts = new List<string>();
        if ((modifiers & 2) != 0) parts.Add("Ctrl");
        if ((modifiers & 1) != 0) parts.Add("Alt");
        if ((modifiers & 4) != 0) parts.Add("Shift");
        if ((modifiers & 8) != 0) parts.Add("Win");

        parts.Add(KeyName(virtualKey));
        return string.Join(" + ", parts);
    }

    private static string KeyName(int virtualKey) =>
        KeyInterop.KeyFromVirtualKey(virtualKey).ToString();

    private void OnCaptureHotkey(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string which) return;

        _capturingHotkey = which;

        var box = which switch
        {
            "Toggle" => HotkeyToggleBox,
            "NextSource" => HotkeyNextSourceBox,
            "BrightnessUp" => HotkeyBrightnessUpBox,
            _ => HotkeyBrightnessDownBox,
        };

        box.Text = "Нажмите сочетание...";
        box.Focus();
        box.PreviewKeyDown -= OnHotkeyKeyDown;
        box.PreviewKeyDown += OnHotkeyKeyDown;
    }

    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        if (_capturingHotkey.Length == 0) return;

        // Сами модификаторы не считаются сочетанием.
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        if (e.Key == Key.Escape)
        {
            _capturingHotkey = "";
            RefreshHotkeyBoxes();
            return;
        }

        int virtualKey = KeyInterop.VirtualKeyFromKey(e.Key);
        int modifiers = 0;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= 1;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= 2;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= 4;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= 8;

        switch (_capturingHotkey)
        {
            case "Toggle":
                _config.Hotkeys.StartStopKey = virtualKey;
                _config.Hotkeys.StartStopModifiers = modifiers;
                break;
            case "NextSource":
                _config.Hotkeys.NextSourceKey = virtualKey;
                _config.Hotkeys.NextSourceModifiers = modifiers;
                break;
            case "BrightnessUp":
                _config.Hotkeys.BrightnessUpKey = virtualKey;
                _config.Hotkeys.BrightnessUpModifiers = modifiers;
                break;
            case "BrightnessDown":
                _config.Hotkeys.BrightnessDownKey = virtualKey;
                _config.Hotkeys.BrightnessDownModifiers = modifiers;
                break;
        }

        _capturingHotkey = "";

        if (sender is TextBox box)
            box.PreviewKeyDown -= OnHotkeyKeyDown;

        RefreshHotkeyBoxes();
        e.Handled = true;
    }

    private void OnApplyHotkeys(object sender, RoutedEventArgs e)
    {
        SaveConfig();
        ApplyHotkeys();

        var text = _hotkeys?.Failed.Count > 0
            ? $"Не удалось занять сочетания (их использует другая программа): {string.Join(", ", _hotkeys.Failed)}"
            : "Горячие клавиши применены.";

        AppendLog(text);
        MessageBox.Show(this, text, "Горячие клавиши", MessageBoxButton.OK,
            _hotkeys?.Failed.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private void OnClearHotkeys(object sender, RoutedEventArgs e)
    {
        _config.Hotkeys = new HotkeyConfig();
        SaveConfig();
        RefreshHotkeyBoxes();
        ApplyHotkeys();
        AppendLog("Все горячие клавиши очищены.");
    }

    private void ApplyHotkeys()
    {
        if (_hotkeys == null) return;

        _hotkeys.UnregisterAll();
        _hotkeys.Failed.Clear();

        _hotkeys.Register(_config.Hotkeys.StartStopKey, _config.Hotkeys.StartStopModifiers,
            "Запуск/остановка", () => Dispatcher.BeginInvoke(new Action(ToggleFromHotkey)));

        _hotkeys.Register(_config.Hotkeys.NextSourceKey, _config.Hotkeys.NextSourceModifiers,
            "Следующий источник", () => Dispatcher.BeginInvoke(new Action(NextSourceFromHotkey)));

        _hotkeys.Register(_config.Hotkeys.BrightnessUpKey, _config.Hotkeys.BrightnessUpModifiers,
            "Яркость больше", () => Dispatcher.BeginInvoke(new Action(() => NudgeBrightness(+10))));

        _hotkeys.Register(_config.Hotkeys.BrightnessDownKey, _config.Hotkeys.BrightnessDownModifiers,
            "Яркость меньше", () => Dispatcher.BeginInvoke(new Action(() => NudgeBrightness(-10))));

        foreach (string failed in _hotkeys.Failed)
            AppendLog($"Сочетание занято другой программой: {failed}");
    }

    private void ToggleFromHotkey()
    {
        if (_engine?.IsRunning == true) StopCapture();
        else StartCapture(silent: true);

        AppendLog($"Горячая клавиша: подсветка {(_engine?.IsRunning == true ? "включена" : "остановлена")}.");
    }

    private void NextSourceFromHotkey()
    {
        int index = (int)_config.ActiveSource;
        index = (index + 1) % 3;
        _config.ActiveSource = (SourceKind)index;

        _loadingUi = true;
        try
        {
            RadioScreen.IsChecked = _config.ActiveSource == SourceKind.Screen;
            RadioMusic.IsChecked = _config.ActiveSource == SourceKind.Music;
            RadioFlash.IsChecked = _config.ActiveSource == SourceKind.Flash;
        }
        finally
        {
            _loadingUi = false;
        }

        MonitorRow.Visibility = _config.ActiveSource == SourceKind.Screen ? Visibility.Visible : Visibility.Collapsed;
        AudioRow.Visibility = _config.ActiveSource == SourceKind.Music ? Visibility.Visible : Visibility.Collapsed;

        AppendLog($"Источник переключён: {SourceFactory.CaptionFor(_config.ActiveSource)}");

        if (_engine?.IsRunning == true) StartCapture(silent: true);
    }

    /// <summary>Горячие клавиши яркости правят максимум: это то, что нужно
    /// на ходу, когда что-то в кадре бьёт яркостью.</summary>
    private void NudgeBrightness(int delta)
    {
        double value = Math.Clamp(MaxBrightnessSlider.Value + delta, 0, 100);
        MaxBrightnessSlider.Value = value;
        AppendLog($"Максимальная яркость: {value:0}%");
    }

    /// <summary>Перехват WM_HOTKEY приходит в оконную процедуру.</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var source = (System.Windows.Interop.HwndSource)PresentationSource.FromVisual(this)!;
        source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_hotkeys?.HandleMessage(msg, wParam) == true)
            handled = true;

        return IntPtr.Zero;
    }

    // ============================================================ прочее

    private void OnSaveSettings(object sender, RoutedEventArgs e)
    {
        _config.AutoStart = AutoStartBox.IsChecked == true;
        _config.StartMinimized = StartMinimizedBox.IsChecked == true;
        _config.AutoStartCapture = AutoStartCaptureBox.IsChecked == true;
        _config.RestoreSceneOnExit = RestoreSceneBox.IsChecked == true;
        _config.AllowBluetoothRecovery = AllowBluetoothRecoveryBox.IsChecked == true;

        SaveConfig();

        AutoStartManager.SetEnabled(_config.AutoStart);

        _service.SendCommand(new IpcMessage { Type = MessageType.ReloadConfig }, 3000);

        AppendLog("Настройки сохранены.");
        MessageBox.Show(this,
            $"Настройки сохранены в:\n{ConfigStore.ConfigPath}",
            "Сохранение", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnReloadSettings(object sender, RoutedEventArgs e)
    {
        _config = ConfigStore.Load();
        LoadUiFromConfig();
        _service.SendCommand(new IpcMessage { Type = MessageType.ReloadConfig }, 3000);
        AppendLog("Настройки перечитаны из файла.");
    }

    private void SaveConfig() => ConfigStore.Save(_config);

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this,
            "OpenTuya Sync\n" +
            "Локальная подсветка для умных ламп: Tuya, WLED, Philips Hue, Bluetooth-ленты.\n\n" +
            "Захват экрана работает через DXGI Desktop Duplication, поэтому подсветка " +
            "следует за полноэкранными играми.\n\n" +
            "Управление устройствами: служба Windows.\n" +
            "Захват и интерфейс: это приложение в сессии пользователя.",
            "О программе", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>
    /// Пишет сообщение в журнал из фонового потока.
    ///
    /// Нужно для событий, которые происходят вне интерфейса (запуск службы
    /// при старте приложения): обращаться к элементам окна можно только из
    /// потока интерфейса, иначе приложение падает.
    /// </summary>
    public void LogFromBackground(string text)
    {
        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                AppendLog(text);

                // Состояние службы изменилось -- обновляем строку состояния.
                CheckServiceStatus();
            }));
        }
        catch (Exception)
        {
            // Окно могло закрыться: журнал не критичен.
        }
    }

    private void AppendLog(string text)
    {
        // Повторяющиеся подряд сообщения не пишутся дважды.
        //
        // Причина: часть сообщений выводится периодически при проверке
        // состояния. Когда ничего не меняется, журнал заполнялся одной и той
        // же строкой каждые несколько секунд, и в нём не было видно главного.
        if (text == _lastLogText)
        {
            _repeatedLogCount++;
            return;
        }

        // Переход к новому сообщению: дописываем счётчик к прошлому.
        if (_repeatedLogCount > 0)
        {
            LogBox.AppendText(
                $"[{DateTime.Now:HH:mm:ss}]   ... то же сообщение ещё {_repeatedLogCount} раз" +
                Environment.NewLine);

            _repeatedLogCount = 0;
        }

        _lastLogText = text;

        string line = $"[{DateTime.Now:HH:mm:ss}] {text}";

        // Журнал ограничен по длине: иначе он растёт бесконечно и начинает
        // сам тормозить отрисовку.
        const int maxLines = 300;
        string[] lines = LogBox.Text.Split('\n');
        if (lines.Length > maxLines)
            LogBox.Text = string.Join('\n', lines.Skip(lines.Length - maxLines));

        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    }

    /// <summary>Последнее выведенное сообщение -- для защиты от повторов.</summary>
    private string? _lastLogText;

    /// <summary>Сколько раз подряд повторилось последнее сообщение.</summary>
    private int _repeatedLogCount;

    /// <summary>Последний состав устройств службы -- чтобы не писать одно и то же.</summary>
    private string? _lastServiceDevices;

    private void RefreshStatus()
    {
        bool running = _engine?.IsRunning == true;

        StatusCapture.Text = running
            ? $"Подсветка: {SourceFactory.CaptionFor(_config.ActiveSource)}"
            : "Подсветка: остановлена";

        StatusBackend.Text = _currentSource is ScreenCaptureSource screen
            ? $"Захват: {screen.BackendName}"
            : "Захват: —";

        StatusFrames.Text = $"Отправлено: {_engine?.SentFrames ?? 0}";

        // Bluetooth-ленты обслуживает приложение, поэтому их состояние
        // показывается отдельно от состояния службы. Подключение может
        // идти до тридцати секунд, поэтому оно видно в строке состояния:
        // без этого ожидание выглядит как зависание.
        //
        // Ошибка показывается вместо бесконечного «подключение...»: раньше
        // при неудаче надпись оставалась навсегда, и причина была непонятна.
        //
        // Пауза показывается отдельно: она означает, что попытки прекращены
        // намеренно, чтобы не занимать эфир и не мешать другим
        // Bluetooth-устройствам поблизости.
        if (_localBle.IsPaused)
        {
            StatusBle.Text = $"Bluetooth: пауза {_localBle.PauseSecondsLeft} с";
        }
        else if (_localBle.IsBusy)
        {
            StatusBle.Text = _lastBleError == null
                ? "Bluetooth: подключение..."
                : "Bluetooth: нет связи";
        }
        else if (_localBle.ConnectedCount > 0)
        {
            StatusBle.Text = $"Bluetooth: {_localBle.ConnectedCount}";
        }
        else
        {
            StatusBle.Text = _lastBleError == null ? "Bluetooth: —" : "Bluetooth: нет связи";
        }

        if (running && _currentSource?.LastError is { } error)
        {
            StatusBackend.Text = $"Захват: ошибка — {error}";
        }

        if (_statusTimerCounter++ % 8 == 0)
            CheckServiceStatus();
    }

    private int _statusTimerCounter;
}
