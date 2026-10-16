using System.Management;

namespace OpenTuyaSync.BtDiag;

/// <summary>
/// Показывает, что подключено к Bluetooth компьютера.
///
/// Зачем. Была жалоба: «служба отключает пульт приставки Xiaomi». Пульт и
/// приставка -- отдельные устройства, компьютер к ним не подключён, поэтому
/// напрямую влиять на их связь программа не может. Чтобы это проверить, а не
/// предполагать, нужен список того, что компьютер действительно держит по
/// Bluetooth: если приставки и пульта там нет, версия о прямом
/// вмешательстве отпадает.
///
/// Данные берутся из системы (PnP): этот способ работает всегда, тогда как
/// перечисление через WinRT на этой машине возвращает пустой список.
///
/// Утилита только читает. Bluetooth-радио она не перезапускает.
///
/// Запуск: OpenTuyaSync.BtDiag.exe
/// </summary>
internal static class Program
{
    /// <summary>Служба, по которой опознаётся светодиодная лента ELK-BLEDOB.</summary>
    private const string LedStripService = "0000fff0";

    private static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        Console.WriteLine("=== Что подключено к Bluetooth компьютера ===");
        Console.WriteLine();

        ReportRadio();
        ReportPairedDevices();
        ReportConnectedLeds();

        Console.WriteLine();
        Console.WriteLine("============================================================");
        Console.WriteLine("Что это значит");
        Console.WriteLine("============================================================");
        Console.WriteLine();
        Console.WriteLine("В списке выше -- устройства, которые подключены к ЭТОМУ компьютеру.");
        Console.WriteLine("Приставки Xiaomi и её пульта здесь быть не должно: они связаны");
        Console.WriteLine("между собой напрямую, компьютер в этой связи не участвует.");
        Console.WriteLine();
        Console.WriteLine("Если приставки и пульта в списке нет, значит программа не может");
        Console.WriteLine("вмешиваться в их связь напрямую -- остаётся только помеха по радио");
        Console.WriteLine("2,4 ГГц или совпадение по времени.");

        return 0;
    }

    /// <summary>Состояние адаптера: если радио выключено, ничего работать не будет.</summary>
    private static void ReportRadio()
    {
        Console.WriteLine("Адаптер Bluetooth:");

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, Status FROM Win32_PnPEntity WHERE PNPClass = 'Bluetooth'");

            foreach (var device in searcher.Get())
            {
                string name = device["Name"]?.ToString() ?? "";
                string status = device["Status"]?.ToString() ?? "";

                // Служебные узлы пропускаем: интересен только сам адаптер.
                if (!name.Contains("Radio") && !name.Contains("Adapter") && !name.Contains("Адаптер"))
                    continue;

                Console.WriteLine($"  {name,-40} {status}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  не удалось прочитать: {ex.Message}");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Сопряжённые устройства. Сопряжение означает, что компьютер знает
    /// ключи устройства и может подключаться к нему сам.
    /// </summary>
    private static void ReportPairedDevices()
    {
        Console.WriteLine("Сопряжённые устройства (компьютер может подключаться к ним сам):");

        int count = 0;

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DeviceID, Status FROM Win32_PnPEntity WHERE DeviceID LIKE 'BTHENUM%' OR DeviceID LIKE 'BTHLE%'");

            foreach (var device in searcher.Get())
            {
                string name = device["Name"]?.ToString() ?? "(без имени)";
                string status = device["Status"]?.ToString() ?? "";
                string id = device["DeviceID"]?.ToString() ?? "";

                Console.WriteLine($"  {name,-40} {status,-10} {ShortenId(id)}");
                count++;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  не удалось прочитать: {ex.Message}");
        }

        if (count == 0)
            Console.WriteLine("  (нет -- ни одно Bluetooth-устройство не сопряжено)");

        Console.WriteLine();
    }

    /// <summary>
    /// Ищет светодиодные ленты среди подключённых устройств.
    ///
    /// Нужно, чтобы отличить «программа держит ленту» от «программа держит
    /// что-то ещё»: это единственное Bluetooth-устройство, с которым
    /// программа работает.
    /// </summary>
    private static void ReportConnectedLeds()
    {
        Console.WriteLine("Светодиодные ленты среди подключённых:");

        int found = 0;

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DeviceID, Status FROM Win32_PnPEntity WHERE DeviceID LIKE '%" + LedStripService + "%'");

            foreach (var device in searcher.Get())
            {
                Console.WriteLine($"  {device["Name"]} -- {device["Status"]}");
                found++;
            }
        }
        catch (Exception)
        {
            // Службы GATT могут быть недоступны без подключения.
        }

        if (found == 0)
            Console.WriteLine("  (лента сейчас не подключена -- это нормально, когда подсветка выключена)");

        Console.WriteLine();
    }

    private static string ShortenId(string id)
    {
        // Идентификатор длинный; оставляем часть с адресом устройства.
        int index = id.IndexOf("DEV_", StringComparison.Ordinal);

        if (index < 0) return id.Length > 40 ? id[..40] : id;

        string tail = id[index..];
        return tail.Length > 30 ? tail[..30] : tail;
    }
}
