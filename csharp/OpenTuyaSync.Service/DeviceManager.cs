using OpenTuyaSync.Core;
using OpenTuyaSync.Core.Ipc;
using OpenTuyaSync.Providers;

namespace OpenTuyaSync.Service;

/// <summary>
/// Держит подключения ко всем включённым устройствам и рассылает им цвет.
///
/// Разделение обязанностей: агент (сессия пользователя) занимается
/// захватом экрана и интерфейсом, а сервис -- устройствами. Так подсветка
/// продолжает работать, даже когда окно агента закрыто, а сами протоколы
/// живут в одном процессе с одной точкой отказа.
/// </summary>
public sealed class DeviceManager : IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<string, IColorProvider> _providers = new();

    private AppConfig _config = new();
    private volatile bool _running;

    /// <summary>Ограничение частоты рассылки. Ставится с запасом: каждый
    /// провайдер дополнительно ограничивает свою отправку.</summary>
    private const int BroadcastIntervalMs = 0;

    public IReadOnlyCollection<DeviceStatus> Statuses
    {
        get
        {
            lock (_sync)
            {
                return _providers.Select(kv =>
                {
                    var device = _config.Devices.FirstOrDefault(d => d.Id == kv.Key);
                    return new DeviceStatus
                    {
                        Id = kv.Key,
                        Name = device?.Name ?? kv.Value.Caption,
                        Connected = kv.Value.IsConnected,
                        Status = kv.Value.StatusText,
                    };
                }).ToList();
            }
        }
    }

    public void LoadConfig()
    {
        _config = ConfigStore.Load();
    }

    public AppConfig Config => _config;

    /// <summary>Подключается ко всем включённым устройствам. Ошибка одного
    /// устройства не мешает остальным -- как и в Python-версии.</summary>
    public (int Connected, List<string> Errors) ConnectAll()
    {
        var errors = new List<string>();
        int connected = 0;

        lock (_sync)
        {
            DisconnectAllInternal();

            foreach (var device in _config.Devices.Where(IsServedHere))
            {
                try
                {
                    var provider = ProviderFactory.Create(device);
                    provider.Connect();
                    _providers[device.Id] = provider;
                    connected++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{device.Name}: {ex.Message}");
                }
            }
        }

        _running = _providers.Count > 0;
        return (connected, errors);
    }

    /// <summary>
    /// Обслуживается ли устройство этой службой.
    ///
    /// Bluetooth-устройства исключены намеренно: служба работает в
    /// изолированной сессии 0, и устройства Bluetooth Low Energy ей
    /// недоступны -- операции GATT требуют сессии пользователя. На практике
    /// лента находится поиском из приложения и не находится из службы.
    /// Такие устройства обслуживает приложение (см. LocalBleDevices).
    /// </summary>
    private static bool IsServedHere(DeviceConfig device) =>
        device.Enabled && device.Type != DeviceKind.ElkBledob;

    public void DisconnectAll()
    {
        lock (_sync)
        {
            DisconnectAllInternal();
        }
        _running = false;
    }

    private void DisconnectAllInternal()
    {
        foreach (var provider in _providers.Values)
        {
            try
            {
                if (_config.RestoreSceneOnExit) provider.RestorePreviousState();
                provider.Disconnect();
            }
            catch (Exception) { }
            try { provider.Dispose(); } catch (Exception) { }
        }
        _providers.Clear();
    }

    /// <summary>
    /// Рассылает цвет. Вызывается из потока обработки канала, поэтому
    /// обязан быть быстрым -- вся сетевая работа уходит в рабочие потоки
    /// внутри провайдеров.
    /// </summary>
    public void ApplyColor(Rgb color)
    {
        if (!_running) return;

        List<IColorProvider> targets;
        lock (_sync) targets = _providers.Values.ToList();

        foreach (var provider in targets)
        {
            try
            {
                provider.SetColor(color);
            }
            catch (Exception)
            {
                // Разовый сбой отправки не должен ронять рассылку.
            }
        }
    }

    public void SetPower(string? deviceId, bool on)
    {
        List<IColorProvider> targets;
        lock (_sync)
        {
            targets = deviceId == null
                ? _providers.Values.ToList()
                : _providers.TryGetValue(deviceId, out var one) ? new List<IColorProvider> { one } : new List<IColorProvider>();
        }

        foreach (var provider in targets)
        {
            try
            {
                if (on) provider.TurnOn();
                else provider.TurnOff();
            }
            catch (Exception) { }
        }
    }

    /// <summary>Сохраняет текущее состояние устройств перед изменением.</summary>
    public void SaveStates()
    {
        List<IColorProvider> targets;
        lock (_sync) targets = _providers.Values.ToList();

        foreach (var provider in targets)
        {
            try { provider.SavePreviousState(); } catch (Exception) { }
        }
    }

    public void Dispose()
    {
        DisconnectAll();
    }
}
