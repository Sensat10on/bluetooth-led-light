using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTuyaSync.Core;
using OpenTuyaSync.Service;

// Точка входа сервиса OpenTuya Sync.
//
// Режимы запуска:
//   OpenTuyaSync.Service.exe                        -- обычный запуск (служба Windows)
//   OpenTuyaSync.Service.exe --console              -- то же, но видно в консоли
//   OpenTuyaSync.Service.exe install [параметры]    -- установить службу
//   OpenTuyaSync.Service.exe uninstall              -- удалить службу
//
// Про захват экрана: служба Windows работает в изолированной сессии 0 и
// НЕ видит рабочий стол пользователя. Это ограничение операционной
// системы, а не реализации. Поэтому захват делает агент в сессии
// пользователя, а служба отвечает только за устройства и рассылку цвета.

var arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
string command = arguments.FirstOrDefault(a => !a.StartsWith("--"))?.ToLowerInvariant() ?? "";

if (command == "install")
{
    return ServiceInstaller.Install(arguments);
}

if (command == "uninstall")
{
    return ServiceInstaller.Uninstall();
}

if (command == "status")
{
    return ServiceInstaller.PrintStatus();
}

// Каталог данных: рядом с исполняемым файлом. Служба и агент должны
// читать один и тот же config.json.
ConfigStore.DataDirectory = AppContext.BaseDirectory;

// Предустановленные устройства из аргументов службы (задаются при
// установке через install --preset "...").
foreach (string preset in ExtractPresets(arguments))
{
    ControlWorker.PresetDevices.AddRange(ServiceInstaller.MaterializePresets(new[] { preset }));
}

bool consoleMode = arguments.Contains("--console");

var builder = Host.CreateApplicationBuilder(args);

if (!consoleMode)
{
    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = ServiceInstaller.ServiceName;
    });
}

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});
builder.Logging.SetMinimumLevel(LogLevel.Information);

builder.Services.AddSingleton<DeviceManager>();
builder.Services.AddSingleton<ControlPipeServer>();
builder.Services.AddSingleton<ControlWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ControlWorker>());

var host = builder.Build();
host.Run();
return 0;

static IEnumerable<string> ExtractPresets(string[] args)
{
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--preset" && i + 1 < args.Length)
            yield return args[i + 1];
        else if (args[i].StartsWith("--preset=", StringComparison.OrdinalIgnoreCase))
            yield return args[i]["--preset=".Length..];
    }
}
