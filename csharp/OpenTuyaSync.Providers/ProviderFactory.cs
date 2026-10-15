using OpenTuyaSync.Core;
using OpenTuyaSync.Providers.Tuya;

namespace OpenTuyaSync.Providers;

/// <summary>
/// Создаёт провайдер нужного типа по настройкам устройства. Добавить новый
/// бренд -- значит добавить одну ветку switch и один класс.
/// </summary>
public static class ProviderFactory
{
    public static IColorProvider Create(DeviceConfig config) => config.Type switch
    {
        DeviceKind.Tuya => new TuyaProvider(config),
        DeviceKind.Wled => new WledProvider(config),
        DeviceKind.Hue => new HueProvider(config),
        DeviceKind.ElkBledob => new ElkBledobProvider(config),
        _ => throw new NotSupportedException($"Неизвестный тип устройства: {config.Type}"),
    };

    public static string CaptionFor(DeviceKind kind) => kind switch
    {
        DeviceKind.Tuya => "Tuya / Smart Life (LSC, Nedis, Mirabella, Treatlife, Ledvance SMART+ WiFi)",
        DeviceKind.Wled => "WLED (ленты ESP8266/ESP32) — НЕ ПРОВЕРЕНО на железе",
        DeviceKind.Hue => "Philips Hue — НЕ ПРОВЕРЕНО на железе",
        DeviceKind.ElkBledob => "ELK-BLEDOB / LotusLamp X (Bluetooth) — НЕ ПРОВЕРЕНО на железе",
        _ => kind.ToString(),
    };
}
