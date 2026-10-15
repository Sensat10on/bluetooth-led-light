using System.Diagnostics;
using System.Net.Sockets;
using OpenTuyaSync.Core;
using OpenTuyaSync.Core.Ipc;

namespace OpenTuyaSync.Agent;

/// <summary>
/// Клиент связи со службой.
///
/// Соединение открывается на каждую операцию и закрывается сразу после неё.
/// Так состояние связи всегда соответствует действительности: постоянное
/// соединение здесь ненадёжно, потому что служба может перезапуститься, а
/// клиент об этом не узнает и будет считать мёртвое соединение рабочим.
/// Именно так и происходило, когда связь шла через именованный канал.
///
/// Соединение с петлевым интерфейсом стоит доли миллисекунды, что на фоне
/// отправки цвета 7-20 раз в секунду незаметно.
/// </summary>
public sealed class ServiceClient : IDisposable
{
    /// <summary>Сколько ждать подключения к службе.</summary>
    private const int ConnectTimeoutMs = 800;

    private volatile bool _lastAttemptSucceeded;
    private volatile string? _lastError;
    private volatile bool _disposed;

    private int _connections;
    private int _failedAttempts;

    /// <summary>Служба отвечала в прошлый раз.</summary>
    public bool IsConnected => _lastAttemptSucceeded;

    /// <summary>Последнее сообщение об ошибке для интерфейса.</summary>
    public string? LastError => _lastError;

    /// <summary>Сколько раз соединение устанавливалось.</summary>
    public int Connections => _connections;

    /// <summary>Сколько обращений закончилось неудачей.</summary>
    public int FailedAttempts => _failedAttempts;

    /// <summary>Путь к журналу связи: сбои иначе не видны, потому что у
    /// оконного приложения нет консоли.</summary>
    public static string LogPath { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "service_link.log");

    /// <summary>Подробный журнал обмена. Выключен, чтобы не писать файл
    /// при обычной работе.</summary>
    public bool Verbose { get; set; }

    private void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff}  {message}\n");
        }
        catch (Exception)
        {
            // Журнал не критичен для работы подсветки.
        }
    }

    /// <summary>Открывает соединение со службой или возвращает null.</summary>
    private TcpClient? TryConnect()
    {
        if (_disposed) return null;

        TcpClient? client = null;
        try
        {
            client = new TcpClient { NoDelay = true };

            // Подключение с таймаутом: интерфейс не должен подвисать,
            // когда служба не запущена.
            var connectTask = client.ConnectAsync(IpcJson.Host, IpcJson.Port);

            if (!connectTask.Wait(ConnectTimeoutMs))
            {
                client.Dispose();
                Log($"служба не ответила за {ConnectTimeoutMs} мс");
                return null;
            }

            Interlocked.Increment(ref _connections);
            return client;
        }
        catch (Exception ex)
        {
            try { client?.Dispose(); } catch (Exception) { }
            Log($"подключение не удалось: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Отправляет сообщение и, если нужно, читает ответ. При обрыве делает
    /// одну повторную попытку с новым соединением.
    /// </summary>
    private IpcMessage? Exchange(IpcMessage message, bool expectResponse, int timeoutMs)
    {
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            var client = TryConnect();

            if (client == null)
            {
                _lastAttemptSucceeded = false;
                _lastError = "служба не отвечает";
                continue;
            }

            using (client)
            {
                try
                {
                    using var stream = client.GetStream();
                    stream.ReadTimeout = timeoutMs;
                    stream.WriteTimeout = timeoutMs;

                    var watch = Stopwatch.StartNew();
                    PipeFraming.WriteMessageAsync(stream, message).GetAwaiter().GetResult();

                    if (!expectResponse)
                    {
                        _lastAttemptSucceeded = true;
                        _lastError = null;
                        return null;
                    }

                    using var cts = new CancellationTokenSource(timeoutMs);
                    var response = PipeFraming.ReadMessageAsync(stream, cts.Token)
                        .GetAwaiter().GetResult();

                    _lastAttemptSucceeded = response != null;
                    _lastError = response == null ? "служба закрыла соединение" : null;

                    Log($"обмен {message.Type}: {watch.Elapsed.TotalMilliseconds:0.0} мс, " +
                        $"ответ={(response != null ? "есть" : "нет")}");

                    return response;
                }
                catch (Exception ex)
                {
                    _lastAttemptSucceeded = false;
                    _lastError = ex.Message;
                    Interlocked.Increment(ref _failedAttempts);

                    Log($"обмен {message.Type} не удался (попытка {attempt}): " +
                        $"{ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        return null;
    }

    /// <summary>Отправляет цвет. Ответ не ожидается: это горячий путь.</summary>
    public bool SendColor(Rgb color)
    {
        var (r, g, b) = color.ToBytes();

        Exchange(new IpcMessage
        {
            Type = MessageType.SetColor,
            R = r, G = g, B = b,
        }, expectResponse: false, timeoutMs: 1000);

        return _lastAttemptSucceeded;
    }

    /// <summary>Отправляет команду и ждёт ответ. Для команд интерфейса.</summary>
    public IpcMessage SendCommand(IpcMessage message, int timeoutMs = 20000)
    {
        var response = Exchange(message, expectResponse: true, timeoutMs);

        return response ?? new IpcMessage
        {
            Type = MessageType.Result,
            Ok = false,
            Text = _lastError == null
                ? "Служба не ответила"
                : $"Служба OpenTuya Sync не отвечает: {_lastError}",
        };
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
