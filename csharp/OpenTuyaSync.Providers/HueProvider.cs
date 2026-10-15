using System.Net.Http;
using System.Text;
using System.Text.Json;
using OpenTuyaSync.Core;

namespace OpenTuyaSync.Providers;

/// <summary>
/// Philips Hue через локальный Bridge (API v1 CLIP, обычный HTTP в
/// локальной сети, облако Philips не используется).
///
/// Сопряжение -- обязательный шаг их API: нужно нажать физическую кнопку
/// на Bridge и в течение 30 секунд запросить ключ. Это делает
/// <see cref="Pair"/>.
/// </summary>
public sealed class HueProvider : HttpProviderBase
{
    private readonly string _bridgeIp;
    private readonly string _username;
    private readonly string _lightId;
    private readonly string _base;

    public HueProvider(DeviceConfig config)
        : base(config.Id)
    {
        var h = config.Hue ?? throw new ArgumentException("Не заданы параметры Hue");
        _bridgeIp = h.BridgeIp;
        _username = h.Username;
        _lightId = h.LightId;
        _base = $"http://{_bridgeIp}/api/{_username}/lights/{_lightId}";
    }

    public override DeviceKind Kind => DeviceKind.Hue;
    public override string Caption => "Philips Hue";
    public override string StatusText => IsConnected
        ? $"подключено, лампа {_lightId}"
        : "не подключено";

    protected override int MinSendIntervalMs => 100;

    protected override void ProbeConnection()
    {
        using var response = Http.GetAsync(_base).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
    }

    protected override void SendColorCore(Rgb color)
    {
        var (h, s, v) = color.ToHsv();

        // Hue ждёт оттенок и насыщенность в 0..65535 и 0..254.
        int hue = (int)Math.Round(h / 360.0 * 65535);
        int sat = (int)Math.Round(s * 254);
        int bri = Math.Max(1, (int)Math.Round(v * 254));

        string body = $"{{\"on\":true,\"hue\":{hue},\"sat\":{sat},\"bri\":{bri}}}";
        Put(body);
    }

    protected override void TurnOnCore() => Put("{\"on\":true}");
    protected override void TurnOffCore() => Put("{\"on\":false}");

    private void Put(string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = Http.PutAsync($"{_base}/state", content).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Сопряжение с Bridge. Вызывать сразу после нажатия физической кнопки
    /// (в течение 30 секунд). Возвращает ключ пользователя для настроек.
    /// </summary>
    public static string Pair(string bridgeIp)
    {
        string json = "{\"devicetype\":\"opentuya_sync#pc\"}";
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = Http.PostAsync($"http://{bridgeIp}/api", content).GetAwaiter().GetResult();

        string text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        if (root.GetArrayLength() == 0)
            throw new InvalidOperationException("Bridge не ответил");

        var first = root[0];

        if (first.TryGetProperty("success", out var success) &&
            success.TryGetProperty("username", out var username))
        {
            return username.GetString() ?? throw new InvalidOperationException("Bridge вернул пустой ключ");
        }

        string reason = "неизвестная ошибка";
        if (first.TryGetProperty("error", out var error) &&
            error.TryGetProperty("description", out var desc))
        {
            reason = desc.GetString() ?? reason;
        }

        throw new InvalidOperationException(
            $"Не удалось выполнить сопряжение: {reason}. Вы нажали кнопку на Bridge?");
    }

    /// <summary>Список ламп на мосту: id -> имя. Нужен для выбора в интерфейсе.</summary>
    public static Dictionary<string, string> ListLights(string bridgeIp, string username)
    {
        using var response = Http.GetAsync($"http://{bridgeIp}/api/{username}/lights")
            .GetAwaiter().GetResult();
        string text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

        var result = new Dictionary<string, string>();
        using var doc = JsonDocument.Parse(text);
        foreach (var light in doc.RootElement.EnumerateObject())
        {
            string name = light.Value.TryGetProperty("name", out var n)
                ? n.GetString() ?? light.Name
                : light.Name;
            result[light.Name] = name;
        }
        return result;
    }
}
