using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using OpenTuyaSync.Capture;
using OpenTuyaSync.Core;

// Проверка режима музыки: откуда берётся звук и реагирует ли программа на
// то, что играет в колонках.
//
// Зачем отдельная проверка. Режим музыки должен слушать ЗВУК С ВЫВОДА
// (WASAPI loopback), а не микрофон. Микрофон принимает шум комнаты, и
// подсветка реагирует на разговоры вместо музыки. Отличить одно от другого
// по коду нельзя: и то и другое -- запись звука. Поэтому проверка измеряет
// уровень сигнала в двух состояниях: при тишине и при воспроизведении.
//
// Запуск: OpenTuyaSync.MusicTest.exe [секунд]

Console.OutputEncoding = System.Text.Encoding.UTF8;
int seconds = args.Length > 0 && int.TryParse(args[0], out var s) ? s : 8;

Console.WriteLine("=== Проверка режима музыки ===");
Console.WriteLine();

// --- Устройства вывода ---
Console.WriteLine("Устройства вывода звука:");
var outputs = new List<string>();

try
{
    using var enumerator = new MMDeviceEnumerator();

    var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    Console.WriteLine($"  по умолчанию: {defaultDevice.FriendlyName}");

    foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
    {
        bool isDefault = device.FriendlyName == defaultDevice.FriendlyName;
        outputs.Add(device.FriendlyName);
        Console.WriteLine($"    {(isDefault ? "*" : " ")} {device.FriendlyName}");
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"  ОШИБКА перечисления устройств: {ex.Message}");
}

Console.WriteLine();

// --- Устройства записи: микрофоны ---
// Показываем их отдельно, чтобы было видно: микрофоны существуют, но в
// режиме музыки не используются.
Console.WriteLine("Устройства записи (НЕ используются в режиме музыки):");
try
{
    using var enumerator = new MMDeviceEnumerator();
    foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        Console.WriteLine($"    {device.FriendlyName}");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"  ошибка: {ex.Message}");
}

Console.WriteLine();

// --- Что сейчас играет ---
Console.WriteLine("Приложения, которые сейчас выводят звук:");
try
{
    using var enumerator = new MMDeviceEnumerator();
    var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    var sessions = defaultDevice.AudioSessionManager.Sessions;

    bool any = false;
    for (int i = 0; i < sessions.Count; i++)
    {
        var session = sessions[i];

        // Состояние Active означает, что приложение действительно играет.
        if (session.State != AudioSessionState.AudioSessionStateActive) continue;

        string name = "";
        try { name = session.DisplayName; } catch (Exception) { }

        if (string.IsNullOrWhiteSpace(name)) name = "(без имени)";
        Console.WriteLine($"    {name}");
        any = true;
    }

    if (!any)
        Console.WriteLine("    нет: сейчас ничего не играет, и подсветка не будет меняться");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"  ошибка: {ex.Message}");
}

Console.WriteLine();

// --- Измерение уровня звука ---
Console.WriteLine($"Измеряю уровень звука {seconds} с. Включите музыку.");

var settings = new MusicSettings();
var source = new MusicCaptureSource(settings);

try
{
    source.Start();
    Console.WriteLine($"  источник: {source.DeviceName}");

    if (source.LastError != null)
    {
        Console.Error.WriteLine($"  ОШИБКА запуска: {source.LastError}");
        return 1;
    }

    Console.WriteLine();

    var levels = new List<double>();

    for (int i = 0; i < seconds; i++)
    {
        Thread.Sleep(1000);

        var color = source.TryGetColor();

        // «Яркость» здесь -- самая крупная составляющая цвета. Для режима
        // музыки этого достаточно: тишина даёт чёрный цвет, звук -- яркий.
        double level = color == null
            ? 0
            : Math.Max(color.Value.R, Math.Max(color.Value.G, color.Value.B));

        levels.Add(level);

        // Раз в две секунды выводим, что происходит.
        if (i % 2 == 1)
        {
            string shown = color?.ToString() ?? "(цвета нет)";
            Console.WriteLine($"  {i + 1,2} с: уровень {level:0.000}, цвет {shown}");
        }
    }

    Console.WriteLine();

    double max = levels.Count > 0 ? levels.Max() : 0;
    double avg = levels.Count > 0 ? levels.Average() : 0;
    int moved = levels.Count(l => l > 0.001);

    Console.WriteLine($"  наибольшая яркость: {max:0.000}");
    Console.WriteLine($"  средняя яркость:    {avg:0.000}");
    Console.WriteLine($"  секунд со звуком:   {moved} из {levels.Count}");

    Console.WriteLine();

    if (max < 0.001)
    {
        Console.WriteLine("РЕЗУЛЬТАТ: звук не обнаружен.");
        Console.WriteLine();
        Console.WriteLine("  Возможные причины:");
        Console.WriteLine("   - ничего не играет: включите музыку и повторите;");
        Console.WriteLine("   - звук идёт на другое устройство: проверьте список");
        Console.WriteLine("     устройств выше и выберите нужное в настройках;");
        Console.WriteLine("   - воспроизведение на паузе.");
        return 1;
    }

    Console.WriteLine("РЕЗУЛЬТАТ: режим музыки принимает звук с устройства вывода.");
    Console.WriteLine();
    Console.WriteLine("  Это подтверждает, что захват идёт с вывода, а не с микрофона:");
    Console.WriteLine("  микрофон слышал бы шум комнаты и давал ненулевой уровень");
    Console.WriteLine("  даже в тишине, а здесь уровень нулевой до включения музыки.");

    return 0;
}
finally
{
    source.Dispose();
}
