using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTuyaSync.Core;

namespace OpenTuyaSync.Service;

/// <summary>
/// Основной цикл службы: читает конфигурацию, поднимает канал для агента и
/// подключает устройства.
///
/// Устройства подключаются сразу при старте службы, а не по запросу
/// агента: тогда подсветка работает и без запущенного интерфейса.
/// </summary>
public sealed class ControlWorker : BackgroundService
{
    private readonly ILogger<ControlWorker> _log;
    private readonly DeviceManager _devices;
    private readonly ControlPipeServer _pipe;

    /// <summary>Предустановленные устройства из командной строки службы.</summary>
    public static List<DeviceConfig> PresetDevices { get; } = new();

    /// <summary>Признак того, что конфигурация задана при установке и
    /// менять её через агента нельзя.</summary>
    public static bool ConfigLocked { get; set; }

    public ControlWorker(ILogger<ControlWorker> log, DeviceManager devices, ControlPipeServer pipe)
    {
        _log = log;
        _devices = devices;
        _pipe = pipe;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("OpenTuya Sync: запуск службы управления устройствами");
        _log.LogInformation("Каталог данных: {Dir}", ConfigStore.DataDirectory);

        _devices.LoadConfig();

        // Предустановленные параметры имеют приоритет над файлом: они
        // заданы администратором при установке службы.
        if (PresetDevices.Count > 0)
        {
            _devices.Config.Devices = PresetDevices;
            _devices.Config.AutoStartCapture = true;
            ConfigLocked = true;
            _log.LogInformation("Применены предустановленные устройства: {Count}", PresetDevices.Count);
        }

        _pipe.AllowConfigChanges = !ConfigLocked;
        _pipe.Start();

        var (connected, errors) = _devices.ConnectAll();
        _log.LogInformation("Подключено устройств: {Connected}", connected);

        foreach (string error in errors)
            _log.LogWarning("Не удалось подключить: {Error}", error);

        if (connected > 0 && _devices.Config.RestoreSceneOnExit)
            _devices.SaveStates();

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Штатная остановка службы.
        }

        _log.LogInformation("Остановка службы: возвращаем лампы в прежнее состояние");

        // Только останавливаем приём подключений и возвращаем лампы в
        // прежнее состояние. Освобождение объектов выполняет контейнер
        // зависимостей: если освобождать ещё и здесь, Dispose вызывается
        // дважды, и служба падает при остановке.
        try { _pipe.Stop(); } catch (Exception ex) { _log.LogWarning(ex, "Ошибка остановки канала"); }

        _devices.DisconnectAll();
    }
}
