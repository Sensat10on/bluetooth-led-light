using System.Globalization;

namespace OpenTuyaSync.Core;

/// <summary>
/// Цвет в линейном RGB, каналы 0..1. Структура, а не класс: цвет
/// передаётся на каждый кадр, и аллокация здесь стоила бы мусора в
/// горячем пути.
/// </summary>
public readonly struct Rgb : IEquatable<Rgb>
{
    public readonly float R;
    public readonly float G;
    public readonly float B;

    public Rgb(float r, float g, float b)
    {
        R = Math.Clamp(r, 0f, 1f);
        G = Math.Clamp(g, 0f, 1f);
        B = Math.Clamp(b, 0f, 1f);
    }

    public static Rgb Black => new(0f, 0f, 0f);
    public static Rgb White => new(1f, 1f, 1f);

    public bool IsBlack => R <= 0.001f && G <= 0.001f && B <= 0.001f;

    /// <summary>Максимальная разница каналов с другим цветом (0..1).</summary>
    public float DistanceTo(Rgb other)
    {
        float dr = Math.Abs(R - other.R);
        float dg = Math.Abs(G - other.G);
        float db = Math.Abs(B - other.B);
        return Math.Max(dr, Math.Max(dg, db));
    }

    public (float H, float S, float V) ToHsv()
    {
        float max = Math.Max(R, Math.Max(G, B));
        float min = Math.Min(R, Math.Min(G, B));
        float delta = max - min;

        float h = 0f;
        if (delta > 1e-6f)
        {
            if (max == R) h = 60f * (((G - B) / delta) % 6f);
            else if (max == G) h = 60f * (((B - R) / delta) + 2f);
            else h = 60f * (((R - G) / delta) + 4f);
            if (h < 0f) h += 360f;
        }

        float s = max <= 1e-6f ? 0f : delta / max;
        return (h, s, max);
    }

    public static Rgb FromHsv(float h, float s, float v)
    {
        h = ((h % 360f) + 360f) % 360f;
        s = Math.Clamp(s, 0f, 1f);
        v = Math.Clamp(v, 0f, 1f);

        float c = v * s;
        float x = c * (1f - Math.Abs(((h / 60f) % 2f) - 1f));
        float m = v - c;

        float r, g, b;
        if (h < 60f) { r = c; g = x; b = 0f; }
        else if (h < 120f) { r = x; g = c; b = 0f; }
        else if (h < 180f) { r = 0f; g = c; b = x; }
        else if (h < 240f) { r = 0f; g = x; b = c; }
        else if (h < 300f) { r = x; g = 0f; b = c; }
        else { r = c; g = 0f; b = x; }

        return new Rgb(r + m, g + m, b + m);
    }

    public (byte R, byte G, byte B) ToBytes() =>
        ((byte)MathF.Round(R * 255f), (byte)MathF.Round(G * 255f), (byte)MathF.Round(B * 255f));

    public string ToHex() => $"#{ToBytes().R:X2}{ToBytes().G:X2}{ToBytes().B:X2}";

    public bool Equals(Rgb other) => R == other.R && G == other.G && B == other.B;
    public override bool Equals(object? obj) => obj is Rgb other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(R, G, B);
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"Rgb({R:0.000}, {G:0.000}, {B:0.000})");
}
