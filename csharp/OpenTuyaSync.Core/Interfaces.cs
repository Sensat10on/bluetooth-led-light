namespace OpenTuyaSync.Core;

/// <summary>
/// Источник цвета. Реализации запускают работу в фоне и отдают последний
/// посчитанный цвет через <see cref="TryGetColor"/>, который никогда не
/// блокирует: смешение захвата и сетевой отправки в одном потоке даёт
/// разрывы звука и просадки кадров.
/// </summary>
public interface ICaptureSource : IDisposable
{
    SourceKind Kind { get; }

    /// <summary>Понятное имя для интерфейса.</summary>
    string Caption { get; }

    bool IsRunning { get; }

    /// <summary>Запускает захват. Не блокирует.</summary>
    void Start();

    /// <summary>Останавливает захват и освобождает ресурсы.</summary>
    void Stop();

    /// <summary>Последний цвет или null, если данных ещё нет.</summary>
    Rgb? TryGetColor();

    /// <summary>
    /// Текст последней ошибки захвата, если она была. Нужен, потому что
    /// сбой в фоновом потоке иначе выглядит как «источник запущен, но
    /// цвета нет».
    /// </summary>
    string? LastError { get; }
}

/// <summary>Устройство вывода цвета.</summary>
public interface IColorProvider : IDisposable
{
    DeviceKind Kind { get; }

    /// <summary>Понятное имя для интерфейса.</summary>
    string Caption { get; }

    bool IsConnected { get; }

    /// <summary>Устанавливает соединение. Бросает исключение с причиной.</summary>
    void Connect();

    void Disconnect();

    /// <summary>
    /// Отправляет цвет. Вызывается часто, поэтому обязан быть быстрым и
    /// не бросать исключения при разовой неудачной отправке: соединение
    /// восстанавливается на следующем вызове.
    /// </summary>
    void SetColor(Rgb color);

    void TurnOn();
    void TurnOff();

    /// <summary>Короткий текст состояния для интерфейса.</summary>
    string StatusText { get; }

    /// <summary>Сохраняет состояние лампы перед тем, как её менять.</summary>
    void SavePreviousState();

    /// <summary>Возвращает лампу к состоянию, сохранённому ранее.</summary>
    void RestorePreviousState();
}
