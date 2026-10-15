namespace OpenTuyaSync.Capture;

/// <summary>
/// Создаёт источник нужного типа. Добавить новый источник -- значит
/// добавить одну ветку сюда и один класс.
/// </summary>
public static class SourceFactory
{
    public static Core.ICaptureSource Create(Core.SourceKind kind, Core.AppConfig config) => kind switch
    {
        Core.SourceKind.Screen => new ScreenCaptureSource(config.Screen),
        Core.SourceKind.Music => new MusicCaptureSource(config.Music),
        Core.SourceKind.Flash => new FlashCaptureSource(config.Flash),
        _ => throw new NotSupportedException($"Неизвестный источник: {kind}"),
    };

    public static string CaptionFor(Core.SourceKind kind) => kind switch
    {
        Core.SourceKind.Screen => "Ambilight (экран)",
        Core.SourceKind.Music => "Режим музыки (звук)",
        Core.SourceKind.Flash => "Режим вспышек (стробоскоп)",
        _ => kind.ToString(),
    };

    public static IReadOnlyList<Core.SourceKind> All => new[]
    {
        Core.SourceKind.Screen,
        Core.SourceKind.Music,
        Core.SourceKind.Flash,
    };
}
