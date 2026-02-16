using System.IO;
using System.Text.Json;

namespace SwitchDcrpc.Wpf.Services;

public sealed class ClientConfigStore
{
    private const string DefaultSwitchIp = "YourSwitchIP";
    private const string DefaultPort = "6000";
    private const string DefaultPollIntervalMs = "2000";
    private const string DefaultRpcName = "Playing on Switch";
    private const string DefaultTitleDbPack = "DE.de.json";
    private const bool DefaultConnectOnStartup = false;
    private const bool DefaultStartWithWindows = false;
    private const bool DefaultShowGithubButton = true;
    private readonly string _path;

    public ClientConfigStore()
    {
        _path = Path.Combine(AppContext.BaseDirectory, "DB", "config.json");
    }

    public string PathOnDisk => _path;

    public async Task<ClientConfig> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            if (!File.Exists(_path))
            {
                var cfg = new ClientConfig
                {
                    TitleDbPack = DefaultTitleDbPack,
                    RpcName = DefaultRpcName,
                    SwitchIp = DefaultSwitchIp,
                    Port = DefaultPort,
                    PollIntervalMs = DefaultPollIntervalMs,
                    ConnectOnStartup = DefaultConnectOnStartup,
                    StartWithWindows = DefaultStartWithWindows,
                    ShowGithubButton = DefaultShowGithubButton
                };
                await SaveAsync(cfg, cancellationToken);
                return cfg;
            }

            var json = await File.ReadAllTextAsync(_path, cancellationToken);
            var cfg2 = JsonSerializer.Deserialize<ClientConfig>(json);
            if (cfg2 is not null)
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty(nameof(ClientConfig.ShowGithubButton), out _))
                {
                    cfg2.ShowGithubButton = DefaultShowGithubButton;
                }
            }
            return cfg2 ?? new ClientConfig
            {
                TitleDbPack = DefaultTitleDbPack,
                RpcName = DefaultRpcName,
                SwitchIp = DefaultSwitchIp,
                Port = DefaultPort,
                PollIntervalMs = DefaultPollIntervalMs,
                ConnectOnStartup = DefaultConnectOnStartup,
                StartWithWindows = DefaultStartWithWindows,
                ShowGithubButton = DefaultShowGithubButton
            };
        }
        catch
        {
            return new ClientConfig
            {
                TitleDbPack = DefaultTitleDbPack,
                RpcName = DefaultRpcName,
                SwitchIp = DefaultSwitchIp,
                Port = DefaultPort,
                PollIntervalMs = DefaultPollIntervalMs,
                ConnectOnStartup = DefaultConnectOnStartup,
                StartWithWindows = DefaultStartWithWindows,
                ShowGithubButton = DefaultShowGithubButton
            };
        }
    }

    public async Task SaveAsync(ClientConfig config, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_path, json, cancellationToken);
    }
}

public sealed class ClientConfig
{
    public string? TitleDbPack { get; set; }
    public string? RpcName { get; set; }
    public string? SwitchIp { get; set; }
    public string? Port { get; set; }
    public string? PollIntervalMs { get; set; }
    public bool ConnectOnStartup { get; set; }
    public bool StartWithWindows { get; set; }
    public bool ShowGithubButton { get; set; }
}
