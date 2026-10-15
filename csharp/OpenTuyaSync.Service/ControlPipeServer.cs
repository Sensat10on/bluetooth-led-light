using System.Net;
using System.Net.Sockets;
using OpenTuyaSync.Core.Ipc;

namespace OpenTuyaSync.Service;

/// <summary>
/// Сервер связи с приложением.
///
/// Изначально связь шла через именованный канал, и это оказалось плохим
/// выбором: доступ к каналу ограничивается не только правами доступа, но и
/// обязательным уровнем целостности. Канал, созданный службой (высокая
/// целостность), недоступен приложению, запущенному обычным пользователем,
/// и снять это ограничение можно только меткой целостности в системном
/// списке доступа -- для чего службе нужно включить себе привилегию
/// SeSecurityPrivilege. На практике приложение получало отказ в доступе,
/// а окно показывало «служба не отвечает».
///
/// Локальный сокет на петлевом интерфейсе таких ограничений не имеет:
/// соединение доступно любому процессу на этой же машине. Снаружи порт
/// недоступен, потому что прослушивается только 127.0.0.1.
/// </summary>
public sealed class ControlPipeServer : IDisposable
{
    /// <summary>Порт на петлевом интерфейсе. Выбран в стороне от частых
    /// занятых диапазонов.</summary>
    public const int Port = 51823;

    private readonly DeviceManager _devices;
    private readonly CancellationTokenSource _cts = new();
    private Socket? _listener;
    private Task? _acceptLoop;
    private int _disposed;

    private volatile bool _stopped;

    /// <summary>Разрешено ли агенту менять конфигурацию. Выключается, когда
    /// сервис запущен с предустановленными параметрами и настройки должны
    /// быть неизменяемыми.</summary>
    public bool AllowConfigChanges { get; set; } = true;

    public ControlPipeServer(DeviceManager devices) => _devices = devices;

    public void Start()
    {
        if (_stopped)
        {
            throw new InvalidOperationException(
                "Сервер связи уже остановлен и не может быть запущен повторно");
        }

        var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        // Только петлевой интерфейс: снаружи порт не виден.
        listener.Bind(new IPEndPoint(IPAddress.Loopback, Port));
        listener.Listen(16);

        _listener = listener;
        _acceptLoop = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptAsync(_cts.Token).ConfigureAwait(false);

                // Каждое подключение обслуживается отдельно: приложение
                // может перезапуститься, а служба обязана остаться на связи.
                _ = Task.Run(() => HandleClient(client));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception)
            {
                await Task.Delay(200, _cts.Token).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleClient(Socket client)
    {
        try
        {
            client.NoDelay = true;

            await using var stream = new NetworkStream(client, ownsSocket: true);

            while (!_cts.IsCancellationRequested)
            {
                var message = await PipeFraming.ReadMessageAsync(stream, _cts.Token).ConfigureAwait(false);
                if (message == null) break;

                var response = Handle(message);
                if (response != null)
                    await PipeFraming.WriteMessageAsync(stream, response, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (SocketException) { }
        catch (Exception) { }
    }

    /// <summary>
    /// Обработка одного сообщения. Возвращает null, если ответ не нужен
    /// (горячий путь установки цвета ответа не ждёт).
    /// </summary>
    private IpcMessage? Handle(IpcMessage message)
    {
        switch (message.Type)
        {
            case MessageType.SetColor:
                _devices.ApplyColor(new Core.Rgb(
                    (message.R ?? 0) / 255f,
                    (message.G ?? 0) / 255f,
                    (message.B ?? 0) / 255f));
                return null;   // подтверждений на каждый кадр не ждём

            case MessageType.Ping:
                return new IpcMessage
                {
                    Type = MessageType.Result,
                    Ok = true,
                    Text = "служба на связи",
                    Devices = _devices.Statuses.ToList(),
                };

            case MessageType.ReloadConfig:
                if (!AllowConfigChanges)
                    return Denied("Сервис запущен с предустановленными параметрами");
                _devices.LoadConfig();
                return new IpcMessage
                {
                    Type = MessageType.Result,
                    Ok = true,
                    Text = "конфигурация перечитана",
                };

            case MessageType.ConnectDevices:
            {
                if (!AllowConfigChanges)
                    return Denied("Сервис запущен с предустановленными параметрами");

                var (connected, errors) = _devices.ConnectAll();
                if (connected > 0 && _devices.Config.RestoreSceneOnExit)
                    _devices.SaveStates();

                return new IpcMessage
                {
                    Type = MessageType.Result,
                    Ok = errors.Count == 0,
                    Text = errors.Count == 0
                        ? $"подключено устройств: {connected}"
                        : $"подключено: {connected}; ошибки: {string.Join("; ", errors)}",
                    Devices = _devices.Statuses.ToList(),
                };
            }

            case MessageType.DisconnectDevices:
                _devices.DisconnectAll();
                return new IpcMessage
                {
                    Type = MessageType.Result,
                    Ok = true,
                    Text = "устройства отключены",
                    Devices = _devices.Statuses.ToList(),
                };

            case MessageType.SetDevicePower:
                _devices.SetPower(message.DeviceId, message.On ?? true);
                return null;

            case MessageType.ScanTuya:
            {
                // Сканирование занимает секунды, поэтому уходит на пул
                // потоков: иначе оно заблокировало бы этот поток, а по
                // нему идёт горячий путь установки цвета.
                var found = Task.Run(() => Providers.Tuya.TuyaScanner.Scan(TimeSpan.FromSeconds(12)))
                    .GetAwaiter().GetResult();

                return new IpcMessage
                {
                    Type = MessageType.Result,
                    Ok = true,
                    Text = found.Count == 0
                        ? "В сети не найдено ни одного устройства Tuya"
                        : $"Найдено: {found.Count}",
                    Discovered = found,
                };
            }

            case MessageType.ScanBluetooth:
            {
                var found = Task.Run(() => Providers.ElkBledobProvider.Scan(TimeSpan.FromSeconds(8)))
                    .GetAwaiter().GetResult();

                return new IpcMessage
                {
                    Type = MessageType.Result,
                    Ok = true,
                    Text = found.Count == 0
                        ? "Рядом не найдено ни одного устройства Bluetooth с именем"
                        : $"Найдено: {found.Count}",
                    Discovered = found,
                };
            }

            case MessageType.PairHue:
            {
                string ip = message.Text ?? "";
                try
                {
                    string username = Providers.HueProvider.Pair(ip);
                    return new IpcMessage
                    {
                        Type = MessageType.Result,
                        Ok = true,
                        Text = "Сопряжение выполнено",
                        Username = username,
                    };
                }
                catch (Exception ex)
                {
                    return new IpcMessage
                    {
                        Type = MessageType.Result,
                        Ok = false,
                        Text = ex.Message,
                    };
                }
            }

            case MessageType.TestDevice:
            {
                string id = message.DeviceId ?? "";
                var device = _devices.Config.Devices.FirstOrDefault(d => d.Id == id);
                if (device == null)
                    return new IpcMessage { Type = MessageType.Result, Ok = false, Text = "Устройство не найдено" };

                try
                {
                    using var provider = Providers.ProviderFactory.Create(device);
                    provider.Connect();
                    provider.SetColor(new Core.Rgb(1f, 0.5f, 0.1f));
                    Thread.Sleep(300);
                    return new IpcMessage
                    {
                        Type = MessageType.Result,
                        Ok = true,
                        Text = $"Связь есть: {device.Name}",
                    };
                }
                catch (Exception ex)
                {
                    return new IpcMessage
                    {
                        Type = MessageType.Result,
                        Ok = false,
                        Text = $"{device.Name}: {ex.Message}",
                    };
                }
            }

            default:
                return new IpcMessage
                {
                    Type = MessageType.Result,
                    Ok = false,
                    Text = $"Неизвестная команда: {message.Type}",
                };
        }
    }

    private static IpcMessage Denied(string reason) => new()
    {
        Type = MessageType.Result,
        Ok = false,
        Text = reason,
    };

    /// <summary>
    /// Прекращает приём подключений, не освобождая объект. Вызывается
    /// рабочим циклом службы при остановке: освобождение остаётся за
    /// контейнером зависимостей, иначе Dispose выполнится дважды.
    /// </summary>
    public void Stop()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _stopped = true;

        try { _cts.Cancel(); } catch (Exception) { }

        try { _listener?.Close(); } catch (Exception) { }

        try { _acceptLoop?.Wait(TimeSpan.FromSeconds(1)); } catch (Exception) { }
    }

    public void Dispose()
    {
        Stop();

        try { _listener?.Dispose(); } catch (Exception) { }
        try { _cts.Dispose(); } catch (Exception) { }
    }
}
