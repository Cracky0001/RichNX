using System.Net.Http;
using System.Net.Http.Json;
using SwitchDcrpc.Wpf.Models;

namespace SwitchDcrpc.Wpf.Services;

public sealed class SwitchStateClient
{
    private readonly HttpClient _httpClient;

    public SwitchStateClient(int timeoutMs)
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(timeoutMs)
        };
    }

    public async Task<SwitchState?> FetchStateAsync(string switchIp, int port, CancellationToken cancellationToken)
    {
        try
        {
            var url = new Uri($"http://{switchIp}:{port}/state");
            return await _httpClient.GetFromJsonAsync<SwitchState>(url, cancellationToken);
        }
        catch
        {
            return null;
        }
    }
}
