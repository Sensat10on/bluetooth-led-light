using System.Diagnostics;
using System.Security.Principal;
using OpenTuyaSync.Core;

namespace OpenTuyaSync.Service;

/// <summary>
/// Установка и удаление службы Windows.
///
/// Используется sc.exe, а не управляемые установщики: так не нужны
/// дополнительные зависимости, а результат виден глазами в
/// services.msc. Установка требует прав администратора -- проверяется
/// заранее, чтобы выдать понятную ошибку вместо отказа системы.
///
/// Предустановленные параметры передаются как аргументы запуска службы,
/// поэтому их можно задать при установке:
///
///   OpenTuyaSync.Service.exe install ^
///     --preset "Кухня|tuya|DEVICEID|192.168.1.50|LOCALKEY|3.5" ^
///     --preset "Лента|wled|192.168.1.60"
/// </summary>
public static class ServiceInstaller
{
    public const string ServiceName = "OpenTuyaSync";
    private const string DisplayName = "OpenTuya Sync (управление подсветкой)";

    private const string Description =
        "Управляет лампами Tuya, WLED, Hue и Bluetooth-лентами: принимает цвет " +
        "от приложения OpenTuya Sync и рассылает его устройствам. Захват экрана " +
        "выполняет приложение в сессии пользователя.";

    public static int Install(string[] arguments)
    {
        if (!IsAdministrator())
        {
            Console.Error.WriteLine("Установка службы требует прав администратора.");
            Console.Error.WriteLine("Запустите командную строку от имени администратора и повторите.");
            return 5;
        }

        string exePath = Environment.ProcessPath ?? "";
        if (string.IsNullOrEmpty(exePath))
        {
            Console.Error.WriteLine("Не удалось определить путь к исполняемому файлу.");
            return 1;
        }

        var presets = ParsePresets(arguments);

        // Аргументы, с которыми служба будет запускаться.
        var serviceArgs = new List<string>();
        foreach (var preset in presets)
            serviceArgs.Add($"--preset \"{preset}\"");

        // Останавливаем прежнюю версию службы, если она уже стоит.
        RunSc($"stop {ServiceName}", ignoreErrors: true);
        RunSc($"delete {ServiceName}", ignoreErrors: true);

        string binPath = serviceArgs.Count == 0
            ? $"\"{exePath}\""
            : $"\"{exePath}\" {string.Join(' ', serviceArgs)}";

        int code = RunSc($"create {ServiceName} binPath= \"{binPath}\" start= auto DisplayName= \"{DisplayName}\"");
        if (code != 0)
        {
            Console.Error.WriteLine("Не удалось создать службу.");
            return code;
        }

        RunSc($"description {ServiceName} \"{Description}\"", ignoreErrors: true);

        // delayed-auto: служба стартует после входа в систему, не задерживая
        // загрузку. Для подсветки это правильный компромисс.
        RunSc($"config {ServiceName} start= delayed-auto", ignoreErrors: true);

        // При падении система перезапускает службу с задержкой.
        RunSc($"failure {ServiceName} reset= 86400 actions= restart/5000/restart/10000/restart/30000", ignoreErrors: true);

        code = RunSc($"start {ServiceName}");
        if (code != 0)
        {
            Console.Error.WriteLine("Служба создана, но не запустилась. Проверьте журнал событий Windows.");
            return code;
        }

        PrintStatus();
        return 0;
    }

    public static int Uninstall()
    {
        if (!IsAdministrator())
        {
            Console.Error.WriteLine("Удаление службы требует прав администратора.");
            return 5;
        }

        RunSc($"stop {ServiceName}", ignoreErrors: true);
        int code = RunSc($"delete {ServiceName}");

        if (code == 0)
            Console.WriteLine("Служба удалена.");
        else
            Console.Error.WriteLine("Не удалось удалить службу (возможно, она не установлена).");

        return code;
    }

    public static int PrintStatus()
    {
        Console.WriteLine();
        Console.WriteLine("Состояние службы:");
        RunSc($"query {ServiceName}", ignoreErrors: true);
        Console.WriteLine();
        Console.WriteLine("Управление: services.msc → " + ServiceName);
        return 0;
    }

    /// <summary>
    /// Разбирает предустановленные устройства из аргументов командной
    /// строки. Формат записи: имя|тип|поле1|поле2|...
    ///
    ///   Кухня|tuya|DeviceID|192.168.1.50|LocalKey|3.5
    ///   Лента|wled|192.168.1.60
    ///   Мост|hue|192.168.1.70|ключ|1
    ///   Панель|elk|BE:32:XX:XX:XX:XX
    /// </summary>
    private static List<string> ParsePresets(string[] arguments)
    {
        var result = new List<string>();
        for (int i = 0; i < arguments.Length; i++)
        {
            if (arguments[i] == "--preset" && i + 1 < arguments.Length)
                result.Add(arguments[i + 1]);
            else if (arguments[i].StartsWith("--preset=", StringComparison.OrdinalIgnoreCase))
                result.Add(arguments[i]["--preset=".Length..]);
        }
        return result;
    }

    /// <summary>
    /// Преобразует строки предустановок в конфигурацию устройств. Вызывается
    /// службой при старте.
    /// </summary>
    public static List<DeviceConfig> MaterializePresets(IEnumerable<string> presets)
    {
        var devices = new List<DeviceConfig>();

        foreach (string preset in presets)
        {
            string[] parts = preset.Split('|');
            if (parts.Length < 2) continue;

            string name = parts[0].Trim();
            string type = parts[1].Trim().ToLowerInvariant();

            try
            {
                switch (type)
                {
                    case "tuya":
                        if (parts.Length < 5) continue;
                        devices.Add(new DeviceConfig
                        {
                            Name = name,
                            Type = DeviceKind.Tuya,
                            Tuya = new TuyaSettings
                            {
                                DevId = parts[2].Trim(),
                                Ip = parts[3].Trim(),
                                LocalKey = parts[4].Trim(),
                                Version = parts.Length > 5 && double.TryParse(parts[5], out var v) ? v : 3.5,
                            },
                        });
                        break;

                    case "wled":
                        if (parts.Length < 3) continue;
                        devices.Add(new DeviceConfig
                        {
                            Name = name,
                            Type = DeviceKind.Wled,
                            Wled = new WledSettings { Ip = parts[2].Trim() },
                        });
                        break;

                    case "hue":
                        if (parts.Length < 5) continue;
                        devices.Add(new DeviceConfig
                        {
                            Name = name,
                            Type = DeviceKind.Hue,
                            Hue = new HueSettings
                            {
                                BridgeIp = parts[2].Trim(),
                                Username = parts[3].Trim(),
                                LightId = parts[4].Trim(),
                            },
                        });
                        break;

                    case "elk":
                    case "bluetooth":
                        if (parts.Length < 3) continue;
                        devices.Add(new DeviceConfig
                        {
                            Name = name,
                            Type = DeviceKind.ElkBledob,
                            ElkBledob = new ElkSettings { Mac = parts[2].Trim() },
                        });
                        break;
                }
            }
            catch (Exception)
            {
                // Некорректная запись не должна мешать остальным.
            }
        }

        return devices;
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static int RunSc(string arguments, bool ignoreErrors = false)
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
            if (process == null) return 1;

            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit(15000);

            if (!string.IsNullOrWhiteSpace(output)) Console.WriteLine(output.Trim());
            if (!ignoreErrors && !string.IsNullOrWhiteSpace(error)) Console.Error.WriteLine(error.Trim());

            return process.ExitCode;
        }
        catch (Exception ex)
        {
            if (!ignoreErrors) Console.Error.WriteLine($"sc.exe: {ex.Message}");
            return 1;
        }
    }
}
