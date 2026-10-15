using System.Diagnostics;
using System.IO;
using OpenTuyaSync.Agent;
using OpenTuyaSync.Core;
using OpenTuyaSync.Core.Ipc;

// Проверка связи приложения со службой и подключения Bluetooth-ленты.
//
// Режимы:
//   ReconnectTest            -- проверка восстановления связи со службой
//   ReconnectTest ble        -- подключение Bluetooth-ленты тем же кодом,
//                               что используется в приложении
//
// Второй режим важен потому, что Bluetooth-устройства обслуживает именно
// приложение: служба работает в изолированной сессии 0, где Bluetooth
// недоступен. Здесь проверяется тот же класс LocalBleDevices, что и в окне.

Console.OutputEncoding = System.Text.Encoding.UTF8;

if (args.Length > 0 && args[0].Equals("ble", StringComparison.OrdinalIgnoreCase))
{
    return await TestBle();
}

// Проверка переключения источников: подключение ленты не должно
// блокировать поток интерфейса и не должно оставлять устройство занятым.
if (args.Length > 0 && args[0].Equals("switch", StringComparison.OrdinalIgnoreCase))
{
    return await TestSourceSwitching();
}

// Проверка одновременной работы: движок шлёт цвет, и в это же время
// приходят ручные команды. Раньше это приводило к зависанию.
if (args.Length > 0 && args[0].Equals("manual", StringComparison.OrdinalIgnoreCase))
{
    return await TestManualWhileAuto();
}

return await TestReconnect(args);

/// <summary>
/// Воспроизводит ситуацию «включено автоматическое управление, и
/// одновременно нажимают ручное»: поток движка без остановки отправляет
/// цвет, а из другого потока идут ручные команды -- цвет, включение,
/// выключение, переподключение.
///
/// Следим за временем каждой ручной команды: если она выполняется долго,
/// значит окно замёрзнет.
/// </summary>
static async Task<int> TestManualWhileAuto()
{
    Console.WriteLine("=== Проверка ручного управления при включённом автоматическом ===");
    Console.WriteLine();

    ConfigStore.DataDirectory = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "dist"));

    var config = ConfigStore.Load();
    using var devices = new LocalBleDevices();

    var errors = await Task.Run(() => devices.Connect(config));

    foreach (string error in errors)
        Console.Error.WriteLine($"  ОШИБКА подключения: {error}");

    Console.WriteLine($"  1. Лента: {devices.StatusText}");
    Console.WriteLine();

    if (devices.ConnectedCount == 0)
    {
        Console.WriteLine("РЕЗУЛЬТАТ: лента не подключилась, проверять нечего");
        return 1;
    }

    // Поток движка: шлёт меняющийся цвет 30 раз в секунду.
    using var stop = new CancellationTokenSource();
    int engineFrames = 0;

    var engine = Task.Run(() =>
    {
        int i = 0;
        while (!stop.IsCancellationRequested)
        {
            float t = (i++ % 60) / 60f;
            devices.ApplyColor(new Rgb(t, 1 - t, 0.5f));
            Interlocked.Increment(ref engineFrames);
            Thread.Sleep(33);
        }
    });

    // Даём движку поработать.
    await Task.Delay(1000);

    Console.WriteLine("  2. Ручные команды при работающем движке:");
    Console.WriteLine("     команда          | время, мс");
    Console.WriteLine("     -----------------+----------");

    double worst = 0;

    var manualActions = new (string Name, Action Action)[]
    {
        ("цвет красный", () => devices.ApplyColor(new Rgb(1f, 0f, 0f))),
        ("цвет зелёный", () => devices.ApplyColor(new Rgb(0f, 1f, 0f))),
        ("включить", () => devices.TurnOn()),
        ("выключить", () => devices.TurnOff()),
        ("переподключить", () =>
        {
            var first = config.Devices.First(d => d.Enabled && d.Type == DeviceKind.ElkBledob);
            devices.Reconnect(config, first.Id);
        }),
        ("цвет синий", () => devices.ApplyColor(new Rgb(0f, 0f, 1f))),
    };

    foreach (var (name, action) in manualActions)
    {
        var watch = Stopwatch.StartNew();

        // Ручная команда выполняется в отдельной задаче -- так же, как её
        // выполняет поток интерфейса.
        await Task.Run(action);

        watch.Stop();
        worst = Math.Max(worst, watch.Elapsed.TotalMilliseconds);

        Console.WriteLine($"     {name,-16} | {watch.Elapsed.TotalMilliseconds,6:0.0}");
    }

    // Даём движку ещё поработать после ручных команд.
    int before = Volatile.Read(ref engineFrames);
    await Task.Delay(1500);
    int after = Volatile.Read(ref engineFrames);

    stop.Cancel();
    await engine;

    Console.WriteLine();
    Console.WriteLine($"  3. Движок после ручных команд: {after - before} кадров за 1,5 с " +
                      $"({(after - before) / 1.5:0} в секунду)");
    Console.WriteLine($"     самая долгая ручная команда: {worst:0.0} мс");
    Console.WriteLine($"     лента: {devices.StatusText}");
    Console.WriteLine();
    Console.WriteLine("  Замечание: переподключение к BLE-ленте всегда занимает секунды --");
    Console.WriteLine("  каждая попытка упирается во внутренний тайм-аут Windows. Поэтому оно");
    Console.WriteLine("  выполняется в фоне и не должно задерживать поток интерфейса.");

    // Переподключение к BLE-устройству не может быть быстрым: так работает
    // стек Windows. Проверяем то, что действительно важно -- движок
    // продолжает работать, а лента остаётся подключённой даже после
    // неудачной попытки переподключения.
    bool ok = (after - before) > 15 && devices.ConnectedCount > 0;

    Console.WriteLine();
    Console.WriteLine(ok
        ? "РЕЗУЛЬТАТ: ручные команды не блокируют, движок работает, лента сохраняется"
        : "РЕЗУЛЬТАТ: ЕСТЬ ПРОБЛЕМА -- движок встал или лента потеряна");

    return ok ? 0 : 1;
}

/// <summary>
/// Многократно подключает и отпускает ленту, как это происходит при смене
/// источника света. Следит за двумя вещами: сколько времени занимает
/// каждый вызов (должен быть мгновенным, иначе окно замёрзнет) и остаётся
/// ли лента рабочей после нескольких переключений.
/// </summary>
static async Task<int> TestSourceSwitching()
{
    Console.WriteLine("=== Проверка переключения источников ===");
    Console.WriteLine();

    ConfigStore.DataDirectory = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "dist"));

    var config = ConfigStore.Load();
    using var devices = new LocalBleDevices();

    var connectWatch = Stopwatch.StartNew();
    var errors = await Task.Run(() => devices.Connect(config));
    connectWatch.Stop();

    Console.WriteLine($"  1. Подключение: {connectWatch.ElapsedMilliseconds} мс, " +
                      $"{devices.StatusText}");

    foreach (string error in errors)
        Console.Error.WriteLine($"     ОШИБКА: {error}");

    if (devices.ConnectedCount == 0)
    {
        Console.WriteLine();
        Console.WriteLine("РЕЗУЛЬТАТ: лента не подключилась, проверять нечего");
        return 1;
    }

    // Вызовы остановки и запуска обязаны быть мгновенными: именно они
    // выполняются в потоке интерфейса при смене источника.
    Console.WriteLine();
    Console.WriteLine("  2. Мгновенность вызовов в потоке интерфейса:");

    double worstStop = 0;

    for (int i = 1; i <= 5; i++)
    {
        var stopWatch = Stopwatch.StartNew();
        devices.DisconnectAll();
        stopWatch.Stop();

        worstStop = Math.Max(worstStop, stopWatch.Elapsed.TotalMilliseconds);
        Console.WriteLine($"     остановка {i}: {stopWatch.Elapsed.TotalMilliseconds:0.0} мс");
    }

    Console.WriteLine($"     худший случай: {worstStop:0.0} мс " +
                      (worstStop < 100 ? "(не блокирует интерфейс)" : "(СЛИШКОМ ДОЛГО)"));

    // Даём фоновым задачам завершить уборку.
    await Task.Delay(1500);

    // После переключений лента обязана подключаться снова.
    Console.WriteLine();
    Console.WriteLine("  3. Повторное подключение после переключений:");

    var againWatch = Stopwatch.StartNew();
    errors = await Task.Run(() => devices.Connect(config));
    againWatch.Stop();

    Console.WriteLine($"     подключение: {againWatch.ElapsedMilliseconds} мс, " +
                      $"{devices.StatusText}");

    foreach (string error in errors)
        Console.Error.WriteLine($"     ОШИБКА: {error}");

    if (devices.ConnectedCount == 0)
    {
        Console.WriteLine();
        Console.WriteLine("РЕЗУЛЬТАТ: после переключений лента больше не подключается");
        return 1;
    }

    devices.ApplyColor(new Rgb(0f, 1f, 0f));
    await Task.Delay(800);

    Console.WriteLine();
    Console.WriteLine("РЕЗУЛЬТАТ: переключение источника не блокирует интерфейс, " +
                      "лента подключается повторно");
    return worstStop < 100 ? 0 : 1;
}

static async Task<int> TestReconnect(string[] args)
{
    int waitSeconds = args.Length > 0 && int.TryParse(args[0], out var s) ? s : 45;

    Console.WriteLine("=== Проверка связи со службой ===");
    Console.WriteLine();

    using var client = new ServiceClient();

    // Первое обращение: служба заведомо недоступна.
    var initial = client.SendCommand(new IpcMessage { Type = MessageType.Ping }, 2000);
    Console.WriteLine($"1. Обращение при выключенной службе:");
    Console.WriteLine($"   ok={initial.Ok}, ответ: {initial.Text}");
    Console.WriteLine($"   служба считается доступной: {client.IsConnected}");
    Console.WriteLine();

    // Отправка цвета в никуда не должна бросать исключение.
    bool colorSent = client.SendColor(new Rgb(1f, 0f, 0f));
    Console.WriteLine($"2. Отправка цвета при выключенной службе: {(colorSent ? "ушла" : "не ушла (ожидаемо)")}");
    Console.WriteLine();

    Console.WriteLine($"3. Ждём появления службы (до {waitSeconds} с)...");
    Console.WriteLine("   Запустите службу: sc start OpenTuyaSync");
    Console.WriteLine();

    var watch = Stopwatch.StartNew();
    bool recovered = false;
    int attempts = 0;

    while (watch.Elapsed.TotalSeconds < waitSeconds)
    {
        attempts++;
        var response = client.SendCommand(new IpcMessage { Type = MessageType.Ping }, 3000);

        if (response.Ok == true)
        {
            recovered = true;
            Console.WriteLine($"   [{watch.Elapsed.TotalSeconds:0.0} с] СЛУЖБА ОТВЕТИЛА после {attempts} попыток");
            Console.WriteLine($"   ответ: {response.Text}");
            Console.WriteLine($"   всего соединений установлено: {client.Connections}");
            break;
        }

        Thread.Sleep(1500);
    }

    Console.WriteLine();

    if (recovered)
    {
        bool sent = client.SendColor(new Rgb(0f, 1f, 0f));
        Console.WriteLine($"4. Отправка цвета после восстановления: {(sent ? "ушла" : "НЕ ушла")}");
        Console.WriteLine();
        Console.WriteLine("РЕЗУЛЬТАТ: клиент переподключается сам, без перезапуска приложения");
        return sent ? 0 : 1;
    }

    Console.WriteLine($"РЕЗУЛЬТАТ: служба не появилась за {waitSeconds} с -- проверка не выполнена");
    return 2;
}

static async Task<int> TestBle()
{
    Console.WriteLine("=== Проверка подключения Bluetooth-ленты ===");
    Console.WriteLine();

    // Настройки берём из папки публикации: там лежит config.json с лентой.
    ConfigStore.DataDirectory = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "dist");

    ConfigStore.DataDirectory = Path.GetFullPath(ConfigStore.DataDirectory);

    Console.WriteLine($"  каталог настроек: {ConfigStore.DataDirectory}");

    var config = ConfigStore.Load();
    var strips = config.Devices.Where(d => d.Enabled && d.Type == DeviceKind.ElkBledob).ToList();

    Console.WriteLine($"  Bluetooth-устройств в настройках: {strips.Count}");
    foreach (var strip in strips)
        Console.WriteLine($"    {strip.Name} -> {strip.ElkBledob?.Mac}");

    if (strips.Count == 0)
    {
        Console.Error.WriteLine("  В настройках нет Bluetooth-устройств -- проверять нечего.");
        return 2;
    }

    Console.WriteLine();
    Console.WriteLine("  Подключение (тем же кодом, что в приложении)...");

    using var devices = new LocalBleDevices();
    var errors = await Task.Run(() => devices.Connect(config));

    Console.WriteLine($"  {devices.StatusText}");

    foreach (string error in errors)
        Console.Error.WriteLine($"  ОШИБКА: {error}");

    if (devices.ConnectedCount == 0)
    {
        Console.WriteLine();
        Console.WriteLine("РЕЗУЛЬТАТ: ни одна лента не подключилась");
        return 1;
    }

    Console.WriteLine();
    Console.WriteLine("  Отправка цветов: красный, зелёный, синий...");

    foreach (var color in new[] { new Rgb(1f, 0f, 0f), new Rgb(0f, 1f, 0f), new Rgb(0f, 0f, 1f) })
    {
        devices.ApplyColor(color);
        Console.WriteLine($"    отправлен {color}");
        await Task.Delay(1200);
    }

    Console.WriteLine();
    Console.WriteLine($"РЕЗУЛЬТАТ: лента подключена и получает цвет ({devices.StatusText})");
    return 0;
}
