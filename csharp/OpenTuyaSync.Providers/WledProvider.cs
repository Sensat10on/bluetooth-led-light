using System.Net.Http;
using System.Text;
using OpenTuyaSync.Core;

namespace OpenTuyaSync.Providers;

/// <summary>
/// WLED (ESP8266/ESP32) через официальный HTTP JSON API.
///
/// Выбран JSON API, а не UDP-протокол realtime: он не требует знать
/// количество светодиодов в ленте, а UDP -- требует, и при неверном
/// значении лента мигает мусором.
/// </summary>
public sealed class WledProvider : HttpProviderBase
{
    private readonly string _baseUrl;

    public WledProvider(DeviceConfig config)
        : base(config.Id)
    {
        var w = config.Wled ?? throw new ArgumentException("Не заданы параметры WLED");
        _baseUrl = $"http://{w.Ip}";
    }

    public override DeviceKind Kind => DeviceKind.Wled;
    public override string Caption => "WLED (ленты ESP8266/ESP32)";
    public override string StatusText => IsConnected
        ? $"подключено, {_baseUrl}"
        : "не подключено";

    /// <summary>WLED рекомендует не чаще 10-12 запросов в секунду.</summary>
    protected override int MinSendIntervalMs => 90;

    protected override void ProbeConnection()
    {
        using var response = Http.GetAsync($"{_baseUrl}/json/info").GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
    }

    protected override void SendColorCore(Rgb color)
    {
        var (r, g, b) = color.ToBytes();
        string body = $"{{\"on\":true,\"seg\":[{{\"col\":[[{r},{g},{b}]]}}]}}";
        Post(body);
    }

    protected override void TurnOnCore() => Post("{\"on\":true}");
    protected override void TurnOffCore() => Post("{\"on\":false}");

    private void Post(string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = Http.PostAsync($"{_baseUrl}/json/state", content).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
    }
}
