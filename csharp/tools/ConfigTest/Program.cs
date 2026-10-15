using OpenTuyaSync.Core;

// Проверка сохранения настроек: запись, чтение и правка устройства.
//
// Отдельная проверка нужна потому, что настройки проходят неочевидный путь:
// интерфейс правит объект, объект сериализуется в snake_case, а при чтении
// имена полей разбираются без учёта регистра. Ошибка в этом месте выглядит
// как «нажал сохранить, а значение не сохранилось».
//
// Запуск: OpenTuyaSync.ConfigTest.exe

Console.OutputEncoding = System.Text.Encoding.UTF8;

// Работаем в отдельном каталоге рядом с проверкой: настоящие настройки не
// трогаем, а временный каталог системы может быть закрыт для записи.
// Имя уникальное, чтобы результат не зависел от прошлого запуска.
string temp = Path.Combine(AppContext.BaseDirectory, "config_test_data");
if (Directory.Exists(temp)) Directory.Delete(temp, true);
Directory.CreateDirectory(temp);

ConfigStore.DataDirectory = temp;

Console.WriteLine("=== Проверка сохранения настроек ===");
Console.WriteLine($"  каталог: {temp}");
Console.WriteLine();

int failures = 0;

// --- 1. Настройки по умолчанию создаются и читаются ---
var created = ConfigStore.Load();
Check("настройки по умолчанию создаются", File.Exists(ConfigStore.ConfigPath), ref failures);

// --- 2. Устройство сохраняется со всеми полями ---
var config = ConfigStore.Load();
config.Devices.Add(new DeviceConfig
{
    Id = "test001",
    Name = "Тестовая лента",
    Type = DeviceKind.ElkBledob,
    Enabled = true,
    BrightnessScale = 0.8f,
    ElkBledob = new ElkSettings { Mac = "BE:60:41:00:27:B9" },
});
ConfigStore.Save(config);

var reloaded = ConfigStore.Load();
var device = reloaded.Devices.FirstOrDefault(d => d.Id == "test001");
Check("устройство сохранилось", device != null, ref failures);

if (device != null)
{
    Check("название сохранилось", device.Name == "Тестовая лента", ref failures, device.Name);
    Check("тип сохранился", device.Type == DeviceKind.ElkBledob, ref failures, device.Type.ToString());
    Check("MAC сохранился", device.ElkBledob?.Mac == "BE:60:41:00:27:B9", ref failures,
        device.ElkBledob?.Mac ?? "нет");

    // Именно это значение не сохранялось при проверке вручную.
    Check("масштаб яркости сохранился", Math.Abs(device.BrightnessScale - 0.8f) < 0.001f,
        ref failures, device.BrightnessScale.ToString("0.###"));
}

// --- 3. Правка значения и повторное сохранение ---
var again = ConfigStore.Load();
var target = again.Devices.First(d => d.Id == "test001");

// Так же, как это делает интерфейс: значение ползунка делится на 100.
target.BrightnessScale = (float)(80.0 / 100.0);
ConfigStore.Save(again);

var afterEdit = ConfigStore.Load().Devices.First(d => d.Id == "test001");
Check("масштаб яркости после правки", Math.Abs(afterEdit.BrightnessScale - 0.8f) < 0.001f,
    ref failures, afterEdit.BrightnessScale.ToString("0.###"));

// --- 4. Пределы яркости Ambilight ---
var limits = ConfigStore.Load();
limits.Screen.BrightnessLimitMin = 20;
limits.Screen.BrightnessLimitMax = 70;
limits.AutoStartCapture = true;
ConfigStore.Save(limits);

var limitsAfter = ConfigStore.Load();
Check("нижний предел яркости сохранился", limitsAfter.Screen.BrightnessLimitMin == 20,
    ref failures, limitsAfter.Screen.BrightnessLimitMin.ToString());
Check("верхний предел яркости сохранился", limitsAfter.Screen.BrightnessLimitMax == 70,
    ref failures, limitsAfter.Screen.BrightnessLimitMax.ToString());
Check("автозапуск подсветки сохранился", limitsAfter.AutoStartCapture, ref failures);

// --- 5. Формат файла: имена полей в snake_case ---
string json = File.ReadAllText(ConfigStore.ConfigPath);
Check("в файле snake_case", json.Contains("brightness_scale"), ref failures);
Check("значение яркости в файле", json.Contains("0.8"), ref failures);

// --- 6. Повреждённый файл не мешает запуску ---
File.WriteAllText(ConfigStore.ConfigPath, "{ это не json");
var recovered = ConfigStore.Load();
Check("повреждённый файл восстанавливается", recovered != null, ref failures);

Console.WriteLine();
Console.WriteLine(failures == 0
    ? "РЕЗУЛЬТАТ: настройки сохраняются и читаются верно"
    : $"РЕЗУЛЬТАТ: ошибок {failures}");

try { Directory.Delete(temp, true); } catch (Exception) { }

return failures == 0 ? 0 : 1;

static void Check(string title, bool ok, ref int failures, string? detail = null)
{
    if (!ok) failures++;

    string suffix = detail == null ? "" : $"  ({detail})";
    Console.WriteLine($"  [{(ok ? "OK" : "ОШИБКА")}] {title}{suffix}");
}
