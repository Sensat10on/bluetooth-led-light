using System.Net.Sockets;
using OpenTuyaSync.Core;
using OpenTuyaSync.Core.Ipc;

// Диагностика связи приложения и службы. Полезна, когда интерфейс пишет
// «служба не отвечает»: этот тест показывает, на каком шаге рвётся связь.
//
// Запуск: OpenTuyaSync.PipeTest.exe [ping|color|scan|status]

string command = args.FirstOrDefault() ?? "ping";

Console.OutputEncoding = System.Text.Encoding.UTF8;

try
{
    Console.WriteLine($"Подключение к службе {IpcJson.Host}:{IpcJson.Port}...");

    using var client = new TcpClient { NoDelay = true };
    var connect = client.ConnectAsync(IpcJson.Host, IpcJson.Port);

    if (!connect.Wait(3000))
    {
        Console.Error.WriteLine("Служба не ответила за 3 секунды.");
        Console.Error.WriteLine("Запустите: OpenTuyaSync.Service.exe --console");
        return 2;
    }

    using var stream = client.GetStream();
    stream.ReadTimeout = 30000;
    Console.WriteLine("Соединение установлено.");

    switch (command)
    {
        case "ping":
        {
            await PipeFraming.WriteMessageAsync(stream, new IpcMessage { Type = MessageType.Ping });
            var response = await PipeFraming.ReadMessageAsync(stream);
            Console.WriteLine($"Ответ: ok={response?.Ok}, текст={response?.Text}");
            if (response?.Devices != null)
                foreach (var device in response.Devices)
                    Console.WriteLine($"  {(device.Connected ? "✔" : "✘")} {device.Name} — {device.Status}");
            break;
        }

        case "status":
        {
            await PipeFraming.WriteMessageAsync(stream, new IpcMessage { Type = MessageType.ConnectDevices });
            var response = await PipeFraming.ReadMessageAsync(stream);
            Console.WriteLine($"Подключение устройств: ok={response?.Ok}, текст={response?.Text}");
            break;
        }

        case "scan":
        {
            Console.WriteLine("Сканирование сети (до 15 секунд)...");
            await PipeFraming.WriteMessageAsync(stream, new IpcMessage { Type = MessageType.ScanTuya });
            var response = await PipeFraming.ReadMessageAsync(stream);
            Console.WriteLine($"Результат: {response?.Text}");
            if (response?.Discovered != null)
                foreach (var device in response.Discovered)
                    Console.WriteLine($"  {device.Caption}");
            break;
        }

        case "color":
        {
            // Отправляем 20 кадров плавно меняющегося цвета и замеряем,
            // сколько времени занимает каждый вызов. Это горячий путь
            // подсветки, и он должен укладываться в единицы миллисекунд.
            Console.WriteLine("Отправка 20 кадров цвета...");
            var sw = System.Diagnostics.Stopwatch.StartNew();

            for (int i = 0; i < 20; i++)
            {
                float t = i / 20f;
                var message = new IpcMessage
                {
                    Type = MessageType.SetColor,
                    R = (int)(255 * t),
                    G = (int)(255 * (1 - t)),
                    B = 128,
                };

                var frameWatch = System.Diagnostics.Stopwatch.StartNew();
                await PipeFraming.WriteMessageAsync(stream, message);
                frameWatch.Stop();
                Console.WriteLine($"  кадр {i + 1}: {frameWatch.Elapsed.TotalMilliseconds:0.00} мс");
                Thread.Sleep(50);
            }

            sw.Stop();
            Console.WriteLine($"Всего: {sw.ElapsedMilliseconds} мс");
            break;
        }

        default:
            Console.WriteLine($"Неизвестная команда: {command}");
            Console.WriteLine("Доступно: ping, status, scan, color");
            break;
    }

    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Ошибка: {ex.GetType().Name}: {ex.Message}");
    return 1;
}
