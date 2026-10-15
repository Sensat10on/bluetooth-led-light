using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenTuyaSync.Core.Ipc;

/// <summary>Типы сообщений между агентом (сессия пользователя) и сервисом.</summary>
public enum MessageType
{
    // --- агент -> сервис ---
    /// <summary>Установить цвет на все включённые устройства. Горячий путь.</summary>
    SetColor,
    /// <summary>Перечитать config.json.</summary>
    ReloadConfig,
    /// <summary>Подключиться ко всем включённым устройствам.</summary>
    ConnectDevices,
    /// <summary>Отключить все устройства и вернуть прежнее состояние.</summary>
    DisconnectDevices,
    /// <summary>Включить/выключить конкретное устройство вручную.</summary>
    SetDevicePower,
    /// <summary>Проверить связь с сервисом.</summary>
    Ping,

    // --- сервис -> агент ---
    /// <summary>Ответ, если запрос его требует.</summary>
    Result,
    /// <summary>Состояние сервиса и устройств.</summary>
    Status,
    /// <summary>Событие, не запрошенное агентом.</summary>
    Event,

    // --- запросы, инициируемые агентом, но выполняемые сервисом ---
    /// <summary>Сканирование локальной сети на устройства Tuya.</summary>
    ScanTuya,
    /// <summary>Сканирование Bluetooth-устройств.</summary>
    ScanBluetooth,
    /// <summary>Сопряжение с мостом Hue.</summary>
    PairHue,
    /// <summary>Проверка связи с конкретным устройством.</summary>
    TestDevice,

    /// <summary>Событие горячей клавиши: агент должен переключить источник.</summary>
    HotkeyCommand,
}

public enum HotkeyAction
{
    ToggleStartStop,
    NextSource,
    BrightnessUp,
    BrightnessDown,
}

/// <summary>Одно сообщение конверта. Поля необязательные: используется
/// один класс вместо иерархии, чтобы протокол оставался простым и его
/// можно было читать глазами в логе.</summary>
public sealed class IpcMessage
{
    public MessageType Type { get; set; }

    /// <summary>Цвет для SetColor. 0..255 на канал.</summary>
    public int? R { get; set; }
    public int? G { get; set; }
    public int? B { get; set; }

    /// <summary>Успех операции для Result.</summary>
    public bool? Ok { get; set; }

    /// <summary>Текст результата или ошибки.</summary>
    public string? Text { get; set; }

    /// <summary>Устройство, к которому относится команда.</summary>
    public string? DeviceId { get; set; }

    /// <summary>Включить или выключить (SetDevicePower).</summary>
    public bool? On { get; set; }

    /// <summary>Действие для HotkeyCommand.</summary>
    public HotkeyAction? Action { get; set; }

    /// <summary>Состояние устройств для Status.</summary>
    public List<DeviceStatus>? Devices { get; set; }

    /// <summary>Найденные при сканировании устройства.</summary>
    public List<DiscoveredDevice>? Discovered { get; set; }

    /// <summary>Локальный ключ, полученный при сопряжении Hue.</summary>
    public string? Username { get; set; }
}

public sealed class DeviceStatus
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Connected { get; set; }
    public string Status { get; set; } = "";
}

public sealed class DiscoveredDevice
{
    /// <summary>Для Tuya: Device ID. Для Bluetooth: MAC-адрес.</summary>
    public string Id { get; set; } = "";
    public string Address { get; set; } = "";
    public string Name { get; set; } = "";
    public double Version { get; set; }
    public bool IsBluetooth { get; set; }

    /// <summary>Строка для списка в интерфейсе.</summary>
    public string Caption => IsBluetooth
        ? (string.IsNullOrWhiteSpace(Name) ? "(без имени)" : Name) + "  ·  " + Address
        : Address + "  ·  " + Id + "  ·  v" + Version.ToString("0.0");
}

/// <summary>
/// Общая точка настройки JSON для протокола. Компактный формат: имена
/// полей в camelCase, null не пишутся -- сообщение о цвете уходит 30-60
/// раз в секунду, и лишние байты тут не бесплатны.
/// </summary>
public static class IpcJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static byte[] Serialize(IpcMessage message) =>
        JsonSerializer.SerializeToUtf8Bytes(message, Options);

    public static IpcMessage? Deserialize(ReadOnlySpan<byte> data) =>
        JsonSerializer.Deserialize<IpcMessage>(data, Options);

    /// <summary>
    /// Порт связи на петлевом интерфейсе.
    ///
    /// Именованный канал здесь не подошёл: доступ к каналу, созданному
    /// службой с высокой целостностью, ограничивается обязательным уровнем
    /// целостности вызывающего процесса. Приложение обычного пользователя
    /// получало отказ в доступе, а окно показывало «служба не отвечает».
    /// Снять ограничение можно только меткой в системном списке доступа,
    /// для чего службе нужна привилегия SeSecurityPrivilege. Локальный
    /// сокет таких ограничений не имеет.
    /// </summary>
    public const int Port = 51823;

    /// <summary>Адрес связи: только петлевой интерфейс, снаружи недоступен.</summary>
    public const string Host = "127.0.0.1";
}
