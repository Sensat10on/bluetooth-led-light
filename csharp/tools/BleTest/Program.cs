using System.Globalization;
using OpenTuyaSync.Providers;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Radios;

// Диагностика Bluetooth-ленты (ELK-BLEDOB / LotusLamp X).
//
// Режимы:
//   BleTest scan [секунд]          -- найти устройства рядом
//   BleTest info <MAC>             -- показать службы и характеристики
//   BleTest color <MAC> R G B      -- отправить цвет
//   BleTest power <MAC> on|off     -- включить или выключить
//
// MAC указывается в виде BE:32:XX:XX:XX:XX.

Console.OutputEncoding = System.Text.Encoding.UTF8;

string command = args.FirstOrDefault()?.ToLowerInvariant() ?? "scan";

switch (command)
{
    case "scan":
        return await Scan(args.Length > 1 && int.TryParse(args[1], out var s) ? s : 10);

    case "info":
        return args.Length > 1 ? await Info(args[1]) : MissingMac();

    case "color":
        return args.Length > 4 ? await Color(args[1], args[2], args[3], args[4]) : MissingMac();

    case "power":
        return args.Length > 2 ? await Power(args[1], args[2]) : MissingMac();

    case "flow":
        return args.Length > 1 ? await Flow(args[1]) : MissingMac();

    default:
        Console.WriteLine("Неизвестная команда. Доступно: scan, info, color, power, flow");
        return 1;
}

/// <summary>
/// Пошаговое повторение логики подключения из провайдера: создание
/// устройства, запрос служб, чтение имени, повторный запрос служб. Нужно,
/// чтобы понять, на каком именно шаге расходится поведение.
/// </summary>
static async Task<int> Flow(string mac)
{
    ulong address = ParseMac(mac);
    var nameCharacteristicId = new Guid("00002a00-0000-1000-8000-00805f9b34fb");
    var writeCharacteristicId = new Guid("0000fff3-0000-1000-8000-00805f9b34fb");

    for (int attempt = 1; attempt <= 3; attempt++)
    {
        Console.WriteLine($"--- Попытка {attempt} ---");

        Windows.Devices.Bluetooth.BluetoothLEDevice? device = null;
        try
        {
            device = await Windows.Devices.Bluetooth.BluetoothLEDevice
                .FromBluetoothAddressAsync(address);

            if (device == null)
            {
                Console.WriteLine("  устройство не найдено");
                return 2;
            }

            Console.WriteLine($"  создано: {device.Name}, связь: {device.ConnectionStatus}");

            var services = await device.GetGattServicesAsync(Windows.Devices.Bluetooth.BluetoothCacheMode.Uncached);
            Console.WriteLine($"  служб: {services.Services.Count} (статус {services.Status})");

            Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristic? nameChar = null;
            bool writeFound = false;

            foreach (var service in services.Services)
            {
                var characteristics = await service.GetCharacteristicsAsync(
                    Windows.Devices.Bluetooth.BluetoothCacheMode.Uncached);

                Console.WriteLine($"    служба {service.Uuid} -> статус {characteristics.Status}, " +
                                  $"характеристик {characteristics.Characteristics.Count}");

                foreach (var c in characteristics.Characteristics)
                {
                    Console.WriteLine($"      {c.Uuid}  ({c.CharacteristicProperties})");
                    if (c.Uuid == nameCharacteristicId) nameChar = c;
                    if (c.Uuid == writeCharacteristicId) writeFound = true;
                }
            }

            if (writeFound)
            {
                Console.WriteLine("  РЕЗУЛЬТАТ: характеристика записи найдена");
                return 0;
            }

            Console.WriteLine($"  связь до чтения: {device.ConnectionStatus}");

            if (nameChar != null)
            {
                Console.WriteLine("  читаю имя устройства, чтобы поднять связь...");
                try
                {
                    var result = await nameChar.ReadValueAsync(
                        Windows.Devices.Bluetooth.BluetoothCacheMode.Uncached);
                    Console.WriteLine($"  чтение: {result.Status}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  чтение не удалось: {ex.GetType().Name}: {ex.Message}");
                }

                Console.WriteLine($"  связь после чтения: {device.ConnectionStatus}");

                // Повторный запрос служб уже после установления связи.
                var again = await device.GetGattServicesAsync(
                    Windows.Devices.Bluetooth.BluetoothCacheMode.Uncached);

                Console.WriteLine($"  повторно служб: {again.Services.Count}");

                foreach (var service in again.Services)
                {
                    var characteristics = await service.GetCharacteristicsAsync(
                        Windows.Devices.Bluetooth.BluetoothCacheMode.Uncached);

                    foreach (var c in characteristics.Characteristics)
                    {
                        Console.WriteLine($"      {c.Uuid}  ({c.CharacteristicProperties})");
                        if (c.Uuid == writeCharacteristicId)
                        {
                            Console.WriteLine("  РЕЗУЛЬТАТ: характеристика записи найдена после чтения имени");
                            return 0;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ошибка: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { device?.Dispose(); } catch (Exception) { }
        }

        await Task.Delay(500);
    }

    Console.WriteLine();
    Console.WriteLine("РЕЗУЛЬТАТ: характеристика записи так и не появилась");
    return 1;
}

static int MissingMac()
{
    Console.Error.WriteLine("Не указан MAC-адрес. Пример: BleTest info BE:32:12:34:56:78");
    return 1;
}

// ---------------------------------------------------------------- состояние радио

static async Task<Radio?> GetBluetoothRadioAsync()
{
    var radios = await Radio.GetRadiosAsync();
    foreach (var radio in radios)
        if (radio.Kind == RadioKind.Bluetooth) return radio;

    return null;
}

// ---------------------------------------------------------------------- scan

static async Task<int> Scan(int seconds)
{
    var radio = await GetBluetoothRadioAsync();
    if (radio == null)
    {
        Console.Error.WriteLine("Bluetooth-радио не найдено.");
        return 2;
    }

    Console.WriteLine($"Радио: {radio.Name}, состояние: {radio.State}");
    if (radio.State != RadioState.On)
    {
        Console.Error.WriteLine("Bluetooth выключен. Включите его в параметрах Windows.");
        return 2;
    }

    var found = new Dictionary<ulong, (string Name, short Rssi)>();
    var sync = new object();

    var watcher = new BluetoothLEAdvertisementWatcher
    {
        // Active -- запрашивает у устройства ответ со своим именем:
        // пассивное сканирование видит далеко не все ленты.
        ScanningMode = BluetoothLEScanningMode.Active,
    };

    watcher.Received += (_, e) =>
    {
        string name = e.Advertisement.LocalName;
        lock (sync)
        {
            // Запоминаем лучший уровень сигнала: чем ближе, тем сильнее.
            if (found.TryGetValue(e.BluetoothAddress, out var existing))
            {
                found[e.BluetoothAddress] = (
                    string.IsNullOrWhiteSpace(name) ? existing.Name : name,
                    Math.Max(existing.Rssi, e.RawSignalStrengthInDBm));
            }
            else
            {
                found[e.BluetoothAddress] = (name, e.RawSignalStrengthInDBm);
            }
        }
    };

    Console.WriteLine($"Сканирование {seconds} с... (активный режим)");
    watcher.Start();
    await Task.Delay(seconds * 1000);
    watcher.Stop();

    List<(ulong Address, string Name, short Rssi)> results;
    lock (sync)
    {
        results = found
            .Select(kv => (kv.Key, kv.Value.Name, kv.Value.Rssi))
            .OrderByDescending(x => x.Rssi)
            .ToList();
    }

    Console.WriteLine();
    Console.WriteLine($"Найдено устройств: {results.Count}");
    Console.WriteLine($"{"имя",-32} {"MAC",-18} {"дБм",5}");
    Console.WriteLine(new string('-', 58));

    foreach (var (address, name, rssi) in results)
    {
        string display = string.IsNullOrWhiteSpace(name) ? "(без имени)" : name;
        Console.WriteLine($"{Trim(display, 32),-32} {FormatMac(address),-18} {rssi,5}");
    }

    Console.WriteLine();
    Console.WriteLine("Ленты LotusLamp X обычно называются NBBUFF, LED-, ELK-, LotusLamp");
    Console.WriteLine("или подобным образом. Названия у клонов разные.");

    return results.Count > 0 ? 0 : 3;
}

static string Trim(string text, int max) =>
    text.Length <= max ? text : text[..(max - 1)] + "…";

static string FormatMac(ulong address)
{
    string hex = address.ToString("X12", CultureInfo.InvariantCulture);
    return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
}

// ---------------------------------------------------------------------- info

static async Task<int> Info(string mac)
{
    ulong address = ParseMac(mac);

    Console.WriteLine($"Подключение к {mac}...");
    var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);

    if (device == null)
    {
        Console.Error.WriteLine("Устройство не найдено. Оно должно быть включено и рядом.");
        return 2;
    }

    Console.WriteLine($"  имя: {device.Name}");
    Console.WriteLine($"  состояние соединения: {device.ConnectionStatus}");

    var services = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
    Console.WriteLine($"  служб найдено: {services.Services.Count} (статус {services.Status})");

    foreach (var service in services.Services)
    {
        Console.WriteLine($"    служба {service.Uuid}");

        var characteristics = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
        if (characteristics.Status != Windows.Devices.Bluetooth.GenericAttributeProfile.GattCommunicationStatus.Success)
            continue;

        foreach (var characteristic in characteristics.Characteristics)
        {
            bool writable = (characteristic.CharacteristicProperties &
                             (Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristicProperties.Write |
                              Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristicProperties.WriteWithoutResponse)) != 0;

            Console.WriteLine($"      хар. {characteristic.Uuid}  " +
                              $"свойства: {characteristic.CharacteristicProperties}" +
                              (writable ? "  ← ДОСТУПНА ДЛЯ ЗАПИСИ" : ""));
        }
    }

    device.Dispose();
    return 0;
}

// --------------------------------------------------------------------- color

static async Task<int> Color(string mac, string r, string g, string b)
{
    var config = new OpenTuyaSync.Core.DeviceConfig
    {
        Name = "Лента",
        Type = OpenTuyaSync.Core.DeviceKind.ElkBledob,
        ElkBledob = new OpenTuyaSync.Core.ElkSettings { Mac = mac },
    };

    using var provider = new ElkBledobProvider(config);

    Console.WriteLine($"Подключение к {mac}...");
    try
    {
        provider.Connect();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Не удалось подключиться: {ex.Message}");
        return 2;
    }

    Console.WriteLine($"  подключено: {provider.StatusText}");

    var color = new OpenTuyaSync.Core.Rgb(
        float.Parse(r, CultureInfo.InvariantCulture) / 255f,
        float.Parse(g, CultureInfo.InvariantCulture) / 255f,
        float.Parse(b, CultureInfo.InvariantCulture) / 255f);

    Console.WriteLine($"Отправка цвета {color} (5 раз с интервалом 400 мс)...");

    for (int i = 0; i < 5; i++)
    {
        provider.SetColor(color);
        await Task.Delay(400);
    }

    // Даём рабочему потоку отправить последнюю команду.
    await Task.Delay(600);

    Console.WriteLine($"  соединение: {provider.StatusText}");
    Console.WriteLine(provider.IsConnected
        ? "Команда отправлена. Если лента не поменяла цвет — проверьте UUID характеристики (BleTest info)."
        : "Соединение потеряно: команды, скорее всего, не дошли.");

    return provider.IsConnected ? 0 : 3;
}

// --------------------------------------------------------------------- power

static async Task<int> Power(string mac, string state)
{
    bool on = state.Equals("on", StringComparison.OrdinalIgnoreCase) ||
              state.Equals("вкл", StringComparison.OrdinalIgnoreCase);

    var config = new OpenTuyaSync.Core.DeviceConfig
    {
        Name = "Лента",
        Type = OpenTuyaSync.Core.DeviceKind.ElkBledob,
        ElkBledob = new OpenTuyaSync.Core.ElkSettings { Mac = mac },
    };

    using var provider = new ElkBledobProvider(config);

    Console.WriteLine($"Подключение к {mac}...");
    try
    {
        provider.Connect();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Не удалось подключиться: {ex.Message}");
        return 2;
    }

    Console.WriteLine($"  подключено: {provider.StatusText}");

    if (on) provider.TurnOn();
    else provider.TurnOff();

    await Task.Delay(800);

    Console.WriteLine($"Отправлено: {(on ? "включить" : "выключить")}");

    return provider.IsConnected ? 0 : 3;
}

// ----------------------------------------------------------------------- MAC

static ulong ParseMac(string mac)
{
    string hex = mac.Replace(":", "").Replace("-", "").Trim();
    if (hex.Length != 12)
        throw new ArgumentException($"Некорректный MAC-адрес: {mac}");

    return ulong.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
