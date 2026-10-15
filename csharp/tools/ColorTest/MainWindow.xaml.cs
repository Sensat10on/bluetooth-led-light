using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace OpenTuyaSync.ColorTest;

/// <summary>
/// Программа для проверки подсветки: заливает экран плавно меняющимся
/// градиентом из выбранных цветов.
///
/// Назначение: включить её на весь экран и убедиться, что лампа следует за
/// изображением. Заодно видно, насколько плавно устройство принимает
/// смену цвета и нет ли задержки.
///
/// Отрисовка идёт через DrawingVisual, а не через элементы разметки:
/// градиент перерисовывается 30 раз в секунду, и создавать на каждом кадре
/// объекты кистей в дереве элементов слишком дорого.
/// </summary>
public partial class MainWindow : Window
{
    private enum Effect
    {
        Sweep,

        /// <summary>Цвет держится и плавно переходит в следующий через
        /// заданный интервал. Экран залит одним цветом, а не градиентом.</summary>
        Strobe,

        Diagonal,
        Radial,
        Rotate,
    }

    private const int MinColors = 2;
    private const int MaxColors = 10;

    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _panelTimer;
    private readonly List<ColorEntry> _colors = new();

    /// <summary>Через сколько секунд без движения мыши скрывать панель.</summary>
    private const double PanelHideDelaySeconds = 2.5;

    private DateTime _lastMouseMove = DateTime.UtcNow;

    private Effect _effect = Effect.Sweep;
    private double _phase;
    private double _brightness = 1.0;
    private int _speed = 30;
    private bool _isFullscreen;
    private WindowState _previousState = WindowState.Normal;

    /// <summary>Сколько секунд держать цвет в режиме стробоскопа.</summary>
    private double _strobeHoldSeconds = 1.0;

    /// <summary>Когда начался текущий переход между цветами.</summary>
    private DateTime _strobeTransitionStart = DateTime.UtcNow;

    /// <summary>Индекс цвета, от которого идёт переход.</summary>
    private int _strobeFrom;

    public MainWindow()
    {
        InitializeComponent();

        // Размер заливки берётся у окна: прямоугольник растянут на всю
        // область, отдельных вычислений не требуется.
        SizeChanged += (_, _) => Redraw();

        // Стартовый набор: цвета, по которым сразу видно смену оттенка.
        AddColor(Colors(255, 0, 0));      // красный
        AddColor(Colors(0, 128, 255));    // голубой
        AddColor(Colors(160, 0, 255));    // фиолетовый

        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            // 33 мс -- примерно 30 кадров в секунду: этого достаточно для
            // плавности, и процессор не занимается лишней работой.
            Interval = TimeSpan.FromMilliseconds(33),
        };
        _timer.Tick += OnTick;
        _timer.Start();

        MouseMove += OnMouseMoved;
        MouseDoubleClick += (_, _) => ToggleFullscreen();
        KeyDown += OnKeyDown;

        // Панель скрывается, когда мышь не двигается: иначе она закрывает
        // часть заливки и мешает проверять подсветку.
        _panelTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _panelTimer.Tick += (_, _) =>
        {
            if (ControlPanel.Visibility != Visibility.Visible) return;

            if ((DateTime.UtcNow - _lastMouseMove).TotalSeconds > PanelHideDelaySeconds)
            {
                HidePanel();
                Status("Панель скрыта. Двиньте мышь, чтобы вернуть.");
            }
        };
        _panelTimer.Start();

        UpdateSpeedLabels();
        UpdateColorButtons();

        // Первая отрисовка откладывается: к этому моменту окно уже
        // разложено. Без этого первый кадр оставался пустым.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Redraw));

        Status("Готово. F11 — полный экран, Esc — выход.");
    }

    // ============================================================ цвета

    private static Color Colors(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    private void AddColor(Color color)
    {
        if (_colors.Count >= MaxColors) return;

        var entry = new ColorEntry(color);
        entry.PropertyChanged += (_, _) => Redraw();
        _colors.Add(entry);

        RebuildColorList();
        UpdateColorButtons();
    }

    /// <summary>
    /// Перестраивает список цветов в панели. Для каждого цвета кнопка-образец
    /// (щелчок открывает системный выбор цвета) и подпись с кодом.
    /// </summary>
    private void RebuildColorList()
    {
        ColorList.Items.Clear();

        for (int i = 0; i < _colors.Count; i++)
        {
            var entry = _colors[i];

            var swatch = new Border
            {
                Width = 46,
                Height = 46,
                CornerRadius = new CornerRadius(6),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(entry.Color),
                Margin = new Thickness(0, 0, 6, 6),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "Нажмите, чтобы выбрать цвет",
                Tag = entry,
            };

            swatch.MouseLeftButtonDown += OnSwatchClicked;

            ColorList.Items.Add(swatch);
        }

        ColorCountText.Text = $"выбрано: {_colors.Count} из {MaxColors}";
    }

    private void OnSwatchClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: ColorEntry entry }) return;

        using var dialog = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(entry.Color.R, entry.Color.G, entry.Color.B),
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        entry.Color = Color.FromRgb(dialog.Color.R, dialog.Color.G, dialog.Color.B);
        RebuildColorList();
        Redraw();
        Status($"Цвет изменён: {entry.Hex}");
    }

    private void OnAddColor(object sender, RoutedEventArgs e)
    {
        if (_colors.Count >= MaxColors)
        {
            Status($"Больше {MaxColors} цветов нельзя — это предел для читаемого градиента.");
            return;
        }

        // Новый цвет берём из палитры, разнесённой по кругу оттенков,
        // чтобы он сразу отличался от уже выбранных.
        double hue = (_colors.Count * 360.0 / MaxColors + 30) % 360;
        AddColor(FromHsv(hue, 0.9, 1.0));
        Status($"Добавлен цвет {_colors[^1].Hex}. Всего: {_colors.Count}.");
    }

    private void OnRemoveColor(object sender, RoutedEventArgs e)
    {
        if (_colors.Count <= MinColors)
        {
            Status($"Меньше {MinColors} цветов нельзя — градиенту нужно хотя бы два.");
            return;
        }

        _colors.RemoveAt(_colors.Count - 1);
        RebuildColorList();
        UpdateColorButtons();
        Redraw();
        Status($"Цвет удалён. Осталось: {_colors.Count}.");
    }

    private void UpdateColorButtons()
    {
        AddColorButton.IsEnabled = _colors.Count < MaxColors;
        RemoveColorButton.IsEnabled = _colors.Count > MinColors;
    }

    private static Color FromHsv(double hue, double saturation, double value)
    {
        int hi = (int)Math.Floor(hue / 60) % 6;
        double f = hue / 60 - Math.Floor(hue / 60);

        double v = value;
        double p = value * (1 - saturation);
        double q = value * (1 - f * saturation);
        double t = value * (1 - (1 - f) * saturation);

        double r, g, b;
        switch (hi)
        {
            case 0: r = v; g = t; b = p; break;
            case 1: r = q; g = v; b = p; break;
            case 2: r = p; g = v; b = t; break;
            case 3: r = p; g = q; b = v; break;
            case 4: r = t; g = p; b = v; break;
            default: r = v; g = p; b = q; break;
        }

        return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    // ============================================================ настройки

    private void OnEffectChanged(object sender, RoutedEventArgs e)
    {
        // Событие Checked приходит ещё при разборе XAML: у первой
        // радиокнопки стоит IsChecked="True", и обработчик вызывается
        // раньше, чем созданы остальные элементы. Без этой проверки
        // программа падала при запуске.
        if (EffectStrobe == null || EffectDiagonal == null
            || EffectRadial == null || EffectRotate == null) return;

        _effect = EffectStrobe.IsChecked == true ? Effect.Strobe
            : EffectDiagonal.IsChecked == true ? Effect.Diagonal
            : EffectRadial.IsChecked == true ? Effect.Radial
            : EffectRotate.IsChecked == true ? Effect.Rotate
            : Effect.Sweep;

        _phase = 0;
        _strobeFrom = 0;
        _strobeTransitionStart = DateTime.UtcNow;

        // Настройка интервала нужна только стробоскопу.
        StrobePanel.Visibility = _effect == Effect.Strobe
            ? Visibility.Visible
            : Visibility.Collapsed;

        Redraw();

        Status($"Эффект: {EffectName(_effect)}");
    }

    private static string EffectName(Effect effect) => effect switch
    {
        Effect.Sweep => "переливание",
        Effect.Strobe => "стробоскоп",
        Effect.Diagonal => "по диагонали",
        Effect.Radial => "из центра",
        Effect.Rotate => "вращение",
        _ => effect.ToString(),
    };

    private void OnHoldChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (HoldText == null) return;

        _strobeHoldSeconds = e.NewValue;
        HoldText.Text = $"{e.NewValue:0.0} с";

        // Перезапускаем переход, чтобы новый интервал применился сразу.
        _strobeTransitionStart = DateTime.UtcNow;
    }

    private void OnSpeedChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _speed = (int)e.NewValue;
        UpdateSpeedLabels();
    }

    private void UpdateSpeedLabels()
    {
        if (SpeedText == null) return;

        SpeedText.Text = $"{_speed}";

        // Подсказываем, что означает выбранная скорость: полный проход по
        // всем цветам занимает примерно 10 секунд при 30.
        double seconds = 300.0 / Math.Max(1, _speed);
        Status($"Скорость {_speed}: полный круг по цветам примерно за {seconds:0.0} с");
    }

    private void OnBrightnessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BrightnessText == null) return;

        _brightness = e.NewValue / 100.0;
        BrightnessText.Text = $"{e.NewValue:0}%";
        Redraw();
    }

    // ============================================================ отрисовка

    private void OnTick(object? sender, EventArgs e)
    {
        // Фаза растёт тем быстрее, чем выше скорость. За один полный круг
        // градиент проходит все выбранные цвета.
        _phase += _speed / 300.0 * 0.033;
        if (_phase > 1.0) _phase -= 1.0;

        Redraw();
    }

    private void Redraw()
    {
        if (ScreenRect == null || _colors.Count < MinColors) return;

        // Стробоскоп заливает экран одним цветом, который плавно меняется
        // на следующий. Градиент здесь не строится вовсе.
        if (_effect == Effect.Strobe)
        {
            ScreenRect.Fill = new SolidColorBrush(StrobeColor());
            return;
        }

        // Цвета для градиента: список повторяется дважды, чтобы переход
        // между последним и первым цветом был плавным, а не рывком.
        var stops = BuildStops();

        ScreenRect.Fill = _effect switch
        {
            Effect.Sweep => BuildLinear(stops, new Point(0, 0.5), new Point(1, 0.5), _phase),
            Effect.Diagonal => BuildLinear(stops, new Point(0, 0), new Point(1, 1), _phase),
            Effect.Rotate => BuildLinear(stops, StartPoint(_phase), EndPoint(_phase), 0),
            Effect.Radial => BuildRadial(stops),
            _ => BuildLinear(stops, new Point(0, 0.5), new Point(1, 0.5), _phase),
        };
    }

    /// <summary>
    /// Текущий цвет стробоскопа: плавный переход от одного цвета списка к
    /// следующему.
    ///
    /// Сначала цвет держится заданный интервал, затем переходит в
    /// следующий за такое же время. Переход идёт один раз, а не по кругу:
    /// смена именно переходит, а не переливается.
    /// </summary>
    private Color StrobeColor()
    {
        int count = _colors.Count;
        double elapsed = (DateTime.UtcNow - _strobeTransitionStart).TotalSeconds;
        double step = Math.Max(0.05, _strobeHoldSeconds);

        // Первая половина шага -- цвет стоит, вторая -- переход.
        Color baseColor;

        if (elapsed < step)
        {
            baseColor = _colors[_strobeFrom % count].Color;
        }
        else if (elapsed < step * 2)
        {
            double t = (elapsed - step) / step;

            // Плавное ускорение и торможение: переход выглядит мягким,
            // без рывка в начале и конце.
            t = t * t * (3 - 2 * t);

            baseColor = Lerp(_colors[_strobeFrom % count].Color,
                             _colors[(_strobeFrom + 1) % count].Color, t);
        }
        else
        {
            // Переход завершён -- следующий цвет становится текущим.
            _strobeFrom = (_strobeFrom + 1) % count;
            _strobeTransitionStart = DateTime.UtcNow;
            baseColor = _colors[_strobeFrom].Color;
        }

        return ApplyBrightness(baseColor);
    }

    private static Color Lerp(Color from, Color to, double t)
    {
        return Color.FromRgb(
            (byte)(from.R + (to.R - from.R) * t),
            (byte)(from.G + (to.G - from.G) * t),
            (byte)(from.B + (to.B - from.B) * t));
    }

    private Color ApplyBrightness(Color color)
    {
        if (_brightness >= 1.0) return color;

        return Color.FromRgb(
            (byte)(color.R * _brightness),
            (byte)(color.G * _brightness),
            (byte)(color.B * _brightness));
    }

    /// <summary>
    /// Собирает точки градиента из выбранных цветов с учётом сдвига фазы.
    /// Сдвиг делается перераспределением позиций: цвета «уезжают» по
    /// градиенту, а не подменяются, поэтому переход остаётся плавным.
    /// </summary>
    private GradientStopCollection BuildStops()
    {
        var stops = new GradientStopCollection();
        int count = _colors.Count;

        // Два прохода по списку: второй нужен для бесшовного перехода
        // от последнего цвета к первому.
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < count; i++)
            {
                double position = (i + pass * count) / (double)count - _phase;

                // Оставляем только то, что попадает в видимый диапазон.
                if (position < -0.05 || position > 1.05) continue;

                stops.Add(new GradientStop(
                    ApplyBrightness(_colors[i].Color),
                    Math.Clamp(position, 0, 1)));
            }
        }

        // Градиенту нужны точки на краях: иначе он растянет первый и
        // последний цвет на весь экран.
        if (stops.Count == 0 || stops[0].Offset > 0)
            stops.Insert(0, new GradientStop(_colors[0].Color, 0));

        if (stops[^1].Offset < 1)
            stops.Add(new GradientStop(_colors[^1].Color, 1));

        return stops;
    }

    private static LinearGradientBrush BuildLinear(
        GradientStopCollection stops, Point from, Point to, double offsetShift)
    {
        var brush = new LinearGradientBrush(stops, from, to);

        // Сдвиг начала и конца градиента даёт движение цвета по экрану.
        brush.StartPoint = new Point(from.X - offsetShift, from.Y - offsetShift);
        brush.EndPoint = new Point(to.X - offsetShift, to.Y - offsetShift);
        brush.MappingMode = BrushMappingMode.RelativeToBoundingBox;

        return brush;
    }

    private static RadialGradientBrush BuildRadial(GradientStopCollection stops)
    {
        var brush = new RadialGradientBrush(stops)
        {
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.75,
            RadiusY = 0.75,
        };

        return brush;
    }

    private static Point StartPoint(double phase)
    {
        double angle = phase * 2 * Math.PI;
        return new Point(0.5 - Math.Cos(angle) * 0.5, 0.5 - Math.Sin(angle) * 0.5);
    }

    private static Point EndPoint(double phase)
    {
        double angle = phase * 2 * Math.PI;
        return new Point(0.5 + Math.Cos(angle) * 0.5, 0.5 + Math.Sin(angle) * 0.5);
    }

    // ============================================================ полный экран

    private void OnToggleFullscreen(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        _isFullscreen = !_isFullscreen;

        if (_isFullscreen)
        {
            _previousState = WindowState;

            // Скрываем заголовок и рамку: на весь экран, без полей.
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;

            HidePanel();
            Status("Полный экран. Мышь не двигайте — панель скрыта. F11 или Esc — выход.");
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _previousState;

            ShowPanel();
            Status("Оконный режим.");
        }
    }

    private void OnHidePanel(object sender, RoutedEventArgs e)
    {
        HidePanel();
        Status("Панель скрыта. Двиньте мышь, чтобы вернуть.");
    }

    private void HidePanel() => ControlPanel.Visibility = Visibility.Collapsed;

    private void ShowPanel()
    {
        ControlPanel.Visibility = Visibility.Visible;
        _lastMouseMove = DateTime.UtcNow;
    }

    private void OnMouseMoved(object sender, MouseEventArgs e)
    {
        _lastMouseMove = DateTime.UtcNow;

        if (ControlPanel.Visibility != Visibility.Visible) ShowPanel();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F11:
                ToggleFullscreen();
                break;

            case Key.Escape:
                if (_isFullscreen) ToggleFullscreen();
                else Close();
                break;

            case Key.Space:
                _timer.IsEnabled = !_timer.IsEnabled;
                Status(_timer.IsEnabled ? "Смена цветов продолжена." : "Смена цветов приостановлена (пробел).");
                break;
        }
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private void Status(string text)
    {
        if (StatusText != null) StatusText.Text = text;
    }

    /// <summary>
    /// Переключает эффект по названию. Нужно для самопроверки, чтобы
    /// отрисовать все эффекты по очереди без участия человека.
    /// </summary>
    public void SelfTestSetEffect(string name)
    {
        _effect = name switch
        {
            "Стробоскоп" => Effect.Strobe,
            "По диагонали" => Effect.Diagonal,
            "Из центра" => Effect.Radial,
            "Вращение" => Effect.Rotate,
            _ => Effect.Sweep,
        };

        Redraw();
    }
}

/// <summary>Один цвет в списке. Уведомляет об изменении, чтобы панель и
/// заливка обновились сразу.</summary>
public sealed class ColorEntry : INotifyPropertyChanged
{
    private Color _color;

    public ColorEntry(Color color) => _color = color;

    public Color Color
    {
        get => _color;
        set
        {
            if (_color == value) return;
            _color = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Color)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Hex)));
        }
    }

    public string Hex => $"#{_color.R:X2}{_color.G:X2}{_color.B:X2}";

    public event PropertyChangedEventHandler? PropertyChanged;
}
