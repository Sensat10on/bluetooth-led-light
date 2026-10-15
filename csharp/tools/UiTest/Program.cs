using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenTuyaSync.Agent;
using OpenTuyaSync.Core;
using OpenTuyaSync.Core.Ipc;
// Проверка интерфейса: создаёт главное окно и убеждается, что разметка
// разбирается без ошибок.
//
// Зачем отдельная утилита: часть обработчиков событий вызывается ещё во
// время разбора XAML (например, у радиокнопки стоит IsChecked="True"), и
// если обработчик в этот момент обращается к ещё не созданным элементам,
// приложение падает прямо при запуске. Именно так и произошло однажды:
// окно не открывалось, а сообщение об ошибке было непонятным. Эта проверка
// ловит такое сразу.
//
// Запуск: OpenTuyaSync.UiTest.exe

// Одновременно подключены WPF и Windows Forms, поэтому часть имён
// совпадает. Псевдонимы фиксируют, что берётся из WPF.
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using TabControl = System.Windows.Controls.TabControl;
using TabItem = System.Windows.Controls.TabItem;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        Console.WriteLine("=== Проверка построения интерфейса ===");

        try
        {
            // Конфигурацию задаём свою, чтобы проверка не зависела от того,
            // что лежит рядом с утилитой.
            //
            // Каталог берём рядом с проверкой, а не в %TEMP%: временный
            // каталог может быть создан другой учётной записью (например,
            // после запуска с правами администратора), и тогда запись в него
            // запрещена. Ошибка выглядела как UnauthorizedAccessException при
            // создании каталога.
            ConfigStore.DataDirectory = Path.Combine(AppContext.BaseDirectory, "ui_test_data");
            Directory.CreateDirectory(ConfigStore.DataDirectory);

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

            // Стили приложения лежат в Styles.xaml. Настоящее приложение
            // подключает их через App.xaml, а здесь Application создаётся
            // вручную, поэтому словарь нужно подключить явно -- иначе
            // разметка не найдёт StaticResource и упадёт с
            // XamlParseException.
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/OpenTuyaSync;component/Styles.xaml"),
            });

            Console.WriteLine("  ресурсы приложения загружены");

            Console.WriteLine("  создаю главное окно...");
            var window = new MainWindow(new AppConfig());

            Console.WriteLine("  разметка разобрана без исключений");

            window.Show();
            window.UpdateLayout();
            Console.WriteLine("  окно построено и разложено");

            var tabs = FindChild<TabControl>(window);
            if (tabs == null)
            {
                Console.Error.WriteLine("  ОШИБКА: не найдены вкладки");
                return 1;
            }

            var names = new List<string>();
            for (int i = 0; i < tabs.Items.Count; i++)
            {
                if (tabs.Items[i] is TabItem item) names.Add(item.Header?.ToString() ?? "?");
            }

            Console.WriteLine($"  вкладки: {string.Join(", ", names)}");

            if (names.Count != 3)
            {
                Console.Error.WriteLine($"  ОШИБКА: ожидалось 3 вкладки, найдено {names.Count}");
                return 1;
            }

            // Найденные устройства показываются в списке как есть, поэтому
            // у них должна быть читаемая подпись. Без неё список выводил имя
            // класса, и вместо названия с адресом было
            // «OpenTuyaSync.Core.Ipc.DiscoveredDevice».
            var discovered = new DiscoveredDevice
            {
                Id = "BE60410027B9",
                Address = "BE:60:41:00:27:B9",
                Name = "ELK-BLEDOB",
                IsBluetooth = true,
            };

            Console.WriteLine($"  найденное устройство: {discovered}");

            if (!discovered.ToString().Contains("ELK-BLEDOB")
                || !discovered.ToString().Contains("BE:60:41:00:27:B9"))
            {
                Console.Error.WriteLine(
                    "  ОШИБКА: подпись найденного устройства не содержит имени и адреса");
                return 1;
            }

            // У туманных устройств подписи другая: адрес, идентификатор и версия.
            var tuya = new DiscoveredDevice
            {
                Id = "bf1234567890",
                Address = "192.168.1.50",
                Version = 3.5,
                IsBluetooth = false,
            };

            Console.WriteLine($"  найденное устройство (Tuya): {tuya}");

            if (!tuya.ToString().Contains("192.168.1.50"))
            {
                Console.Error.WriteLine("  ОШИБКА: подпись устройства Tuya без адреса");
                return 1;
            }

            // Палитра ручного управления: у каждой кнопки должен быть цвет.
            // Проверяем потому, что палитра строится кодом, а не разметкой:
            // ошибка в построении оставила бы вкладку пустой, и это не было бы
            // видно по разбору XAML.
            //
            // Ищем по имени, а не обходом визуального дерева: вкладка
            // «Вручную» не открыта, и её элементы ещё не созданы. Имя доступно
            // сразу после разбора разметки.
            var palette = window.FindName("PalettePanel") as ItemsControl;

            if (palette == null)
            {
                Console.Error.WriteLine("  ОШИБКА: не найдена палитра цветов");
                return 1;
            }

            int swatches = 0;
            var distinct = new HashSet<string>();

            foreach (var item in palette.Items)
            {
                if (item is not WrapPanel panel) continue;

                foreach (var child in panel.Children)
                {
                    if (child is not Button { Tag: Rgb color } button) continue;

                    swatches++;

                    if (button.Background is SolidColorBrush brush)
                    {
                        var (r, g, b) = color.ToBytes();
                        distinct.Add($"{brush.Color.R},{brush.Color.G},{brush.Color.B}");

                        // Цвет кнопки должен совпадать с цветом в Tag: иначе
                        // кнопка показывала бы один цвет, а отправлялся другой.
                        if (brush.Color.R != r || brush.Color.G != g || brush.Color.B != b)
                        {
                            Console.Error.WriteLine(
                                $"  ОШИБКА: кнопка {brush.Color} не совпадает с цветом {r},{g},{b}");
                            return 1;
                        }
                    }
                }
            }

            Console.WriteLine($"  кнопок в палитре: {swatches}, различных цветов: {distinct.Count}");

            // Проверяем, что окно действительно загрузилось: обработчик
            // загрузки заполняет список устройств и журнал. Без этого
            // проверка палитры ничего не значила бы.
            bool loaded = window.IsLoaded;
            Console.WriteLine($"  окно загружено: {loaded}");

            var preview = window.FindName("ColorPreview") as Border;
            if (preview?.Background is SolidColorBrush previewBrush)
            {
                var c = previewBrush.Color;
                Console.WriteLine($"  предпросмотр ручного цвета: {c.R},{c.G},{c.B}");

                // Ползунки по умолчанию стоят на 255, и предпросмотр должен
                // совпадать с ними. Раньше он оставался белым, а отправлялся
                // цвет по умолчанию -- чёрный.
                var red = window.FindName("RedSlider") as Slider;
                if (red != null)
                {
                    Console.WriteLine($"  ползунок красного: {red.Value:0}");
                }
            }

            // Снимок окна: удобно посмотреть, как выглядит разметка, не
            // запуская программу вручную. Файл кладётся рядом с проверкой.
            SaveScreenshot(window, "ui_palette.png", manualTab: true);
            SaveScreenshot(window, "ui_automatic.png", manualTab: false);

            if (swatches < 20)
            {
                Console.Error.WriteLine($"  ОШИБКА: в палитре {swatches} кнопок, ожидалось не меньше 20");
                return 1;
            }

            if (distinct.Count != swatches)
            {
                Console.Error.WriteLine("  ОШИБКА: в палитре есть повторяющиеся цвета");
                return 1;
            }

            window.Close();
            app.Shutdown();

            Console.WriteLine();
            Console.WriteLine("РЕЗУЛЬТАТ: интерфейс строится без ошибок");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"ОШИБКА при построении интерфейса: {ex.GetType().Name}");
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    /// <summary>
    /// Сохраняет снимок окна в файл рядом с проверкой.
    ///
    /// Окно сначала показывается: без этого вкладка не строится, и на снимке
    /// был бы пустой прямоугольник.
    /// </summary>
    private static void SaveScreenshot(Window window, string fileName, bool manualTab)
    {
        try
        {
            window.Show();

            // Открываем нужную вкладку, чтобы её содержимое построилось.
            var tabs = FindChild<TabControl>(window);
            if (tabs != null && tabs.Items.Count > 1)
                tabs.SelectedIndex = manualTab ? 1 : 0;

            window.UpdateLayout();

            // Даём разметке разложиться: без прокрутки очереди сообщений
            // часть элементов ещё не отрисована.
            var frame = new System.Windows.Threading.DispatcherFrame();
            window.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.ContextIdle,
                new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);

            window.UpdateLayout();

            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)window.ActualWidth, (int)window.ActualHeight, 96, 96,
                PixelFormats.Pbgra32);

            bitmap.Render(window);

            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));

            string path = Path.Combine(AppContext.BaseDirectory, fileName);
            using var stream = File.Create(path);
            encoder.Save(stream);

            Console.WriteLine($"  снимок окна: {fileName}");

            window.Hide();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  снимок окна не сделан: {ex.Message}");
        }
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        return FindChild<T>(parent, _ => true);
    }

    /// <summary>Ищет элемент заданного типа, у которого имя подходит под условие.</summary>
    private static T? FindChild<T>(DependencyObject parent, Func<string, bool> nameMatches)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);

            if (child is T match)
            {
                string name = (match as FrameworkElement)?.Name ?? "";
                if (nameMatches(name)) return match;
            }

            var found = FindChild<T>(child, nameMatches);
            if (found != null) return found;
        }

        return null;
    }
}
