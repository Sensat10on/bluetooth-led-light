namespace OpenTuyaSync.Core;

/// <summary>
/// Цветовая математика, общая для всех источников. Полностью повторяет
/// логику Python-версии (core/color.py), чтобы поведение лампы не
/// изменилось после переписывания.
/// </summary>
public static class ColorMath
{
    /// <summary>
    /// Подгоняет цвет так, чтобы он хорошо выглядел на светодиоде, а не
    /// блёкло, как на экране.
    /// </summary>
    /// <param name="satBoost">Множитель насыщенности.</param>
    /// <param name="satFloor">Минимальная насыщенность, но только если
    /// цвет уже был настоящим (чистый серый не получает выдуманный
    /// оттенок).</param>
    /// <param name="valueFloor">Минимальная яркость, но только если не
    /// совсем темно (см. <paramref name="blackCutoff"/>).</param>
    /// <param name="blackCutoff">Ниже этой яркости цвет уходит в чёрный
    /// вместо применения valueFloor.</param>
    public static Rgb Enhance(
        Rgb color,
        float satBoost = 1f,
        float satFloor = 0f,
        float valueFloor = 0f,
        float blackCutoff = 0.04f)
    {
        var (h, sRaw, v) = color.ToHsv();

        float s = Math.Min(1f, sRaw * satBoost);
        if (sRaw > 0.02f) s = Math.Max(s, satFloor);
        if (v > blackCutoff) v = Math.Max(v, valueFloor);

        return Rgb.FromHsv(h, s, v);
    }

    /// <summary>
    /// Ограничивает яркость (V в HSV) диапазоном [minV, maxV], не трогая
    /// оттенок и насыщенность. Нужно для автоматических режимов: гасит
    /// всплески вроде флешки в игре.
    /// </summary>
    public static Rgb ClampBrightness(Rgb color, float minV, float maxV)
    {
        if (minV <= 0f && maxV >= 1f) return color;

        var (h, s, v) = color.ToHsv();
        v = Math.Max(minV, Math.Min(maxV, v));
        return Rgb.FromHsv(h, s, v);
    }

    /// <summary>
    /// Экспоненциальное сглаживание цвета между кадрами. alpha -- доля
    /// нового цвета (0 = не менять, 1 = брать новый сразу).
    /// </summary>
    public static Rgb Smooth(Rgb previous, Rgb current, float alpha)
    {
        alpha = Math.Clamp(alpha, 0f, 1f);
        return new Rgb(
            previous.R * (1f - alpha) + current.R * alpha,
            previous.G * (1f - alpha) + current.G * alpha,
            previous.B * (1f - alpha) + current.B * alpha);
    }
}

/// <summary>
/// Приводит сырой уровень (RMS звука, яркость картинки) к 0..1
/// относительно недавнего максимума, а не фиксированного числа. Потолок
/// сразу поднимается, если стало громче, и медленно опускается.
/// </summary>
public sealed class AutoGain
{
    private readonly float _decay;
    private readonly float _floor;
    private float _ceiling;

    public AutoGain(float decay = 0.997f, float floor = 1e-4f)
    {
        _decay = decay;
        _floor = floor;
        _ceiling = floor;
    }

    public float Normalize(float raw)
    {
        _ceiling = Math.Max(raw, Math.Max(_ceiling * _decay, _floor));
        return Math.Min(1f, raw / _ceiling);
    }

    public void Reset() => _ceiling = _floor;
}

/// <summary>
/// Простой детектор удара: сравнивает значение текущего блока со
/// скользящим средним. Не определяет темп, но для реакции света на
/// музыку хватает.
/// </summary>
public sealed class BeatDetector
{
    private readonly float _factor;
    private readonly double _cooldown;
    private readonly int _avgWindow;
    private readonly float[] _history;
    private int _count;
    private int _head;
    private double _nextOk;

    public BeatDetector(float factor = 1.5f, double cooldown = 0.12, int avgWindow = 40)
    {
        _factor = factor;
        _cooldown = cooldown;
        _avgWindow = avgWindow;
        _history = new float[avgWindow];
    }

    public bool Check(float value, double now)
    {
        float sum = 0f;
        for (int i = 0; i < _count; i++) sum += _history[i];
        float avg = _count > 0 ? sum / _count : value;

        _history[_head] = value;
        _head = (_head + 1) % _avgWindow;
        if (_count < _avgWindow) _count++;

        bool isBeat = avg > 1e-6f && value > avg * _factor && now >= _nextOk;
        if (isBeat) _nextOk = now + _cooldown;
        return isBeat;
    }

    public void Reset()
    {
        _count = 0;
        _head = 0;
        _nextOk = 0;
    }
}
