using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenTuyaSync.Agent;
using OpenTuyaSync.Core;

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
            ConfigStore.DataDirectory = Path.Combine(Path.GetTempPath(), "opentuya-uitest");
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

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;

            var found = FindChild<T>(child);
            if (found != null) return found;
        }

        return null;
    }
}
