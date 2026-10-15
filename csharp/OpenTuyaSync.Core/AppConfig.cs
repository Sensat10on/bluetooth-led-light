using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenTuyaSync.Core;

/// <summary>Тип устройства (выход).</summary>
public enum DeviceKind
{
    Tuya,
    Wled,
    Hue,
    ElkBledob,
}

/// <summary>Тип источника цвета.</summary>
public enum SourceKind
{
    /// <summary>Ambilight: цвет с экрана.</summary>
    Screen,
    /// <summary>Реакция на звук из колонок.</summary>
    Music,
    /// <summary>Стробоскоп.</summary>
    Flash,
}

public sealed class TuyaSettings
{
    public string DevId { get; set; } = "";
    public string Ip { get; set; } = "";
    public string LocalKey { get; set; } = "";
    public double Version { get; set; } = 3.5;
}

public sealed class WledSettings
{
    public string Ip { get; set; } = "";
}

public sealed class HueSettings
{
    public string BridgeIp { get; set; } = "";
    public string Username { get; set; } = "";
    public string LightId { get; set; } = "";
}

public sealed class ElkSettings
{
    public string Mac { get; set; } = "";
}

public sealed class DeviceConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";
    public DeviceKind Type { get; set; } = DeviceKind.Tuya;
    public bool Enabled { get; set; } = true;

    public TuyaSettings? Tuya { get; set; }
    public WledSettings? Wled { get; set; }
    public HueSettings? Hue { get; set; }
    public ElkSettings? ElkBledob { get; set; }

    /// <summary>Персональный множитель яркости (0..2). Позволяет выровнять
    /// лампы, которые светят заметно ярче или тусклее остальных.</summary>
    public float BrightnessScale { get; set; } = 1.0f;

    public string TypeCaption => Type switch
    {
        DeviceKind.Tuya => "Tuya",
        DeviceKind.Wled => "WLED",
        DeviceKind.Hue => "Philips Hue",
        DeviceKind.ElkBledob => "ELK-BLEDOB",
        _ => Type.ToString(),
    };
}

public sealed class ScreenSettings
{
    public int Monitor { get; set; } = 1;
    public int IntervalMs { get; set; } = 150;
    public int SampleStep { get; set; } = 14;
    public float Smoothing { get; set; } = 0.65f;
    public float SaturationBoost { get; set; } = 2.4f;
    public float SaturationFloor { get; set; } = 0.35f;
    public float ValueFloor { get; set; } = 0.15f;
    public float BlackCutoff { get; set; } = 0.04f;
    public int BrightnessLimitMin { get; set; }
    public int BrightnessLimitMax { get; set; } = 100;

    /// <summary>Область захвата в долях экрана (0..1). По умолчанию весь
    /// экран. Используется для режима «зоны» (край/угол монитора).</summary>
    public float ZoneLeft { get; set; }
    public float ZoneTop { get; set; }
    public float ZoneWidth { get; set; } = 1f;
    public float ZoneHeight { get; set; } = 1f;
}

public sealed class MusicSettings
{
    public float BeatFactor { get; set; } = 1.5f;
    public double BeatCooldown { get; set; } = 0.12;
    public float MinBrightness { get; set; } = 0.12f;
    public int SendIntervalMs { get; set; } = 120;
    public int BrightnessLimitMin { get; set; }
    public int BrightnessLimitMax { get; set; } = 100;
    /// <summary>Имя устройства вывода. Пусто -- устройство по умолчанию.</summary>
    public string OutputDevice { get; set; } = "";
}

public sealed class FlashSettings
{
    public double Hz { get; set; } = 2.5;
    public bool Colorful { get; set; } = true;
    public int BrightnessLimitMin { get; set; }
    public int BrightnessLimitMax { get; set; } = 100;
}

public sealed class HotkeyConfig
{
    /// <summary>Виртуальный код клавиши Windows (VK_*). 0 = не задана.</summary>
    public int StartStopKey { get; set; }
    /// <summary>Модификаторы: 1=Alt, 2=Ctrl, 4=Shift, 8=Win.</summary>
    public int StartStopModifiers { get; set; }

    public int NextSourceKey { get; set; }
    public int NextSourceModifiers { get; set; }

    public int BrightnessUpKey { get; set; }
    public int BrightnessUpModifiers { get; set; }

    public int BrightnessDownKey { get; set; }
    public int BrightnessDownModifiers { get; set; }
}

public sealed class AppConfig
{
    public List<DeviceConfig> Devices { get; set; } = new();

    public ScreenSettings Screen { get; set; } = new();
    public MusicSettings Music { get; set; } = new();
    public FlashSettings Flash { get; set; } = new();

    public HotkeyConfig Hotkeys { get; set; } = new();

    /// <summary>Запускать агент при входе пользователя в систему.</summary>
    public bool AutoStart { get; set; }

    /// <summary>Запускать агент сразу свёрнутым в трей.</summary>
    public bool StartMinimized { get; set; } = true;

    /// <summary>Восстанавливать прежнее состояние лампы при остановке.</summary>
    public bool RestoreSceneOnExit { get; set; } = true;

    /// <summary>Источник, выбранный последним.</summary>
    public SourceKind ActiveSource { get; set; } = SourceKind.Screen;

    /// <summary>Автоматически запускать выбранный источник при старте
    /// агента (нужно для работы из сервиса без открытия окна).</summary>
    public bool AutoStartCapture { get; set; }
}

/// <summary>
/// Загрузка и сохранение config.json. Намеренно повторяет формат
/// Python-версии, чтобы настройки можно было перенести между версиями.
/// </summary>
public static class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Каталог данных: рядом с exe, а не в Program Files, потому
    /// что конфиг должен быть доступен на запись и сервису, и агенту.</summary>
    public static string DataDirectory { get; set; } = AppContext.BaseDirectory;

    public static string ConfigPath => Path.Combine(DataDirectory, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                string json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, Options);
                if (cfg != null) return cfg;
            }
        }
        catch (Exception)
        {
            // Повреждённый конфиг не должен мешать запуску: работаем на
            // значениях по умолчанию и перезаписываем файл при сохранении.
        }

        var fresh = new AppConfig();
        Save(fresh);
        return fresh;
    }

    public static void Save(AppConfig config)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            string json = JsonSerializer.Serialize(config, Options);

            // Пишем через временный файл: если приложение упадёт посреди
            // записи, конфиг не останется обрезанным.
            string tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, ConfigPath, overwrite: true);
        }
        catch (Exception)
        {
            // Сохранение настроек не критично для работы подсветки.
        }
    }
}
