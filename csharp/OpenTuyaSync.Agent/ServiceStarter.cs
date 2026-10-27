using System.Diagnostics;

namespace OpenTuyaSync.Agent;

/// <summary>
/// Запуск службы OpenTuya Sync из приложения.
///
/// Почему так. Раньше служба стартовала вместе с Windows (тип запуска
/// «автоматически»), то есть запуск выполняла служба. Нужен запуск
/// приложением, свёрнутым в трей: при входе в систему появляется приложение,
/// и оно поднимает службу. Из этого два следствия:
///
///   - служба ставится с типом запуска «вручную» и сама не стартует;
///   - приложение запускает её при старте, без запроса прав администратора.
///
/// Второе возможно потому, что при установке службе выдаётся разрешение на
/// запуск интерактивным пользователям. Иначе при входе в систему каждый раз
/// появлялся бы запрос повышенных прав.
///
/// Работа идёт через sc.exe, как и остальные действия со службой: так не
/// нужна дополнительная зависимость.
/// </summary>
public static class ServiceStarter
{
    public const string ServiceName = "OpenTuyaSync";

    /// <summary>Сколько ждать перехода службы в состояние «работает».</summary>
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Запускает службу, если она установлена и остановлена.
    ///
    /// Возвращает текст для журнала. Ничего не бросает: недоступная служба
    /// не должна мешать запуску приложения -- Bluetooth-ленту обслуживает
    /// приложение, служба нужна только сетевым устройствам.
    /// </summary>
    public static string EnsureRunning()
    {
        try
        {
            var (installed, state) = Query();

            if (!installed)
            {
                return "Служба не установлена: сетевые устройства недоступны. " +
                       "Установите её на вкладке «Настройки».";
            }

            if (state is "RUNNING")
                return "Служба уже работала.";

            if (state is "START_PENDING")
                return "Служба запускается.";

            var (code, output) = Run($"start {ServiceName}");

            if (code != 0)
            {
                return $"Службу запустить не удалось (код {code}): {FirstLine(output)}. " +
                       "Запустите ПЕРЕЗАПУСК-СЛУЖБЫ.bat от имени администратора.";
            }

            // Ждём готовности: обращение к службе сразу после команды
            // запуска ещё не пройдёт, и интерфейс показал бы «не запущена».
            var deadline = DateTime.UtcNow + StartTimeout;

            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(300);

                var (_, current) = Query();

                if (current is "RUNNING")
                    return "Служба запущена приложением.";

                if (current is "STOPPED")
                    return "Служба запустилась и сразу остановилась. Смотрите журнал " +
                           "событий Windows (источник OpenTuyaSync).";
            }

            return "Служба запускается дольше обычного: проверьте состояние на вкладке «Настройки».";
        }
        catch (Exception ex)
        {
            return $"Службу запустить не удалось: {ex.Message}.";
        }
    }

    /// <summary>
    /// Запускает службу в фоне и пишет итог в журнал приложения.
    ///
    /// Отдельный поток нужен потому, что ожидание готовности занимает
    /// секунды: интерфейс не должен ждать.
    /// </summary>
    public static void EnsureRunning(MainWindow window)
    {
        string result;

        try
        {
            result = EnsureRunning();
        }
        catch (Exception ex)
        {
            result = $"Службу запустить не удалось: {ex.Message}";
        }

        window.LogFromBackground(result);
    }

    /// <summary>
    /// Спрашивает у системы, установлена ли служба и в каком она состоянии.
    /// </summary>
    private static (bool Installed, string State) Query()
    {
        var (_, output) = Run($"query {ServiceName}");

        // Неустановленная служба даёт код 1060 и пустой разбор.
        if (output.Contains("1060", StringComparison.Ordinal))
            return (false, "");

        // Строка состояния выглядит так:
        //   STATE              : 4  RUNNING
        foreach (string line in output.Split('\n'))
        {
            int index = line.IndexOf("STATE", StringComparison.OrdinalIgnoreCase);
            if (index < 0) continue;

            string rest = line[(index + "STATE".Length)..].Trim();

            // Убираем двоеточие и код состояния, оставляя слово.
            string[] parts = rest.Split(
                new[] { ':', ' ' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (string part in parts)
            {
                if (part.Length > 2 && char.IsLetter(part[0]))
                    return (true, part.ToUpperInvariant());
            }
        }

        return (true, "");
    }

    private static (int Code, string Output) Run(string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo("sc.exe", arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(startInfo);

            if (process == null) return (1, "");

            string output = process.StandardOutput.ReadToEnd() +
                            process.StandardError.ReadToEnd();

            process.WaitForExit(20000);

            return (process.ExitCode, output);
        }
        catch (Exception ex)
        {
            return (1, ex.Message);
        }
    }

    private static string FirstLine(string text)
    {
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0) return trimmed;
        }

        return "без подробностей";
    }
}
