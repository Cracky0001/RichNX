using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Collections.ObjectModel;
using SwitchDcrpc.Wpf.Infrastructure;
using SwitchDcrpc.Wpf.Models;
using SwitchDcrpc.Wpf.Services;

namespace SwitchDcrpc.Wpf.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private const string HardcodedDiscordAppId = "1472632678929924399";
    private const string DefaultSwitchIp = "YourSwitchIP";
    private const string DefaultPort = "6029";
    private const string DefaultPollIntervalMs = "2000";
    private const string DefaultRpcName = "Playing on Switch";
    private const string DefaultTitleDbPack = "DE.de.json";
    private const string GithubRepoUrl = "https://github.com/Cracky0001/RichNX";
    private const string GithubButtonLabel = "Download from GitHub";
    private static readonly TimeSpan StateUnreachableClearDelay = TimeSpan.FromSeconds(10);
    private string _switchIp = DefaultSwitchIp;
    private string _port = DefaultPort;
    private string _discordAppId = HardcodedDiscordAppId;
    private string _pollIntervalMs = DefaultPollIntervalMs;
    private string _rpcName = DefaultRpcName;
    private string _statusText = "Stopped";
    private string _currentGameText = "Current game: -";
    private string _lastError = string.Empty;
    private string _logText = string.Empty;
    private bool _isRunning;

    private CancellationTokenSource? _cts;
    private DiscordIpcClient? _discord;
    private ActivityPayload? _lastActivity;
    private Task? _runTask;
    private readonly TitleListStore _titles = new();
    private Task? _titlesTask;
    private ulong _lastMissingLoggedTid;
    private readonly ClientConfigStore _configStore = new();
    private readonly TitleDbPackStore _titledb = new();
    private Task? _titledbTask;
    private string _selectedTitleDbPack = DefaultTitleDbPack;
    private DateTimeOffset? _stateUnreachableSinceUtc;
    private bool _rpcClearedWhileUnreachable;
    private bool _connectOnStartup;
    private bool _startWithWindows;
    private bool _showGithubButton = true;
    private bool _showBatteryStatus = true;
    private string? _presenceSessionKey;
    private long _presenceSessionStartUnix;

    public MainViewModel()
    {
        StartCommand = new RelayCommand(Start, () => !IsRunning);
        StopCommand = new RelayCommand(Stop, () => IsRunning);
        UpdateTitleDbCommand = new RelayCommand(() => _ = UpdateTitleDbAsync(forceDownload: true), () => true);
        TitleDbPacks = new ObservableCollection<string>(_titledb.Packs);
        _ = LoadConfigAtStartupAsync();
    }

    public string SwitchIp
    {
        get => _switchIp;
        set => SetProperty(ref _switchIp, value);
    }

    public string Port
    {
        get => _port;
        set => SetProperty(ref _port, DefaultPort);
    }

    public string DiscordAppId
    {
        get => _discordAppId;
        set => SetProperty(ref _discordAppId, HardcodedDiscordAppId);
    }

    public string PollIntervalMs
    {
        get => _pollIntervalMs;
        set => SetProperty(ref _pollIntervalMs, value);
    }

    public string RpcName
    {
        get => _rpcName;
        set => SetProperty(ref _rpcName, value);
    }

    public bool ConnectOnStartup
    {
        get => _connectOnStartup;
        set => SetProperty(ref _connectOnStartup, value);
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set => SetProperty(ref _startWithWindows, value);
    }

    public bool ShowGithubButton
    {
        get => _showGithubButton;
        set => SetProperty(ref _showGithubButton, value);
    }

    public bool ShowBatteryStatus
    {
        get => _showBatteryStatus;
        set => SetProperty(ref _showBatteryStatus, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string CurrentGameText
    {
        get => _currentGameText;
        private set => SetProperty(ref _currentGameText, value);
    }

    public string LastError
    {
        get => _lastError;
        private set => SetProperty(ref _lastError, value);
    }

    public string LogText
    {
        get => _logText;
        private set => SetProperty(ref _logText, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                ((RelayCommand)StartCommand).RaiseCanExecuteChanged();
                ((RelayCommand)StopCommand).RaiseCanExecuteChanged();
                ((RelayCommand)UpdateTitleDbCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand UpdateTitleDbCommand { get; }

    public ObservableCollection<string> TitleDbPacks { get; }

    public string SelectedTitleDbPack
    {
        get => _selectedTitleDbPack;
        set
        {
            if (SetProperty(ref _selectedTitleDbPack, value))
            {
                _ = UpdateTitleDbAsync(forceDownload: false);
            }
        }
    }

    public async Task ShutdownAsync()
    {
        try
        {
            await _configStore.SaveAsync(BuildCurrentConfig(), CancellationToken.None);
        }
        catch
        {
            // Ignore config save errors during shutdown.
        }

        Stop();
        if (_runTask is not null)
        {
            try
            {
                await _runTask;
            }
            catch
            {
                // Ignore shutdown exceptions.
            }
        }
    }

    private void Start()
    {
        if (!ValidateInputs(out var port, out var pollMs))
        {
            return;
        }

        _ = _configStore.SaveAsync(BuildCurrentConfig(), CancellationToken.None);
        _runTask = RunAsync(port, pollMs);
    }

    private async Task RunAsync(int port, int pollMs)
    {
        if (IsRunning)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _discord = new DiscordIpcClient(HardcodedDiscordAppId);
        var stateClient = new SwitchStateClient(1500);
        _lastActivity = null;
        _stateUnreachableSinceUtc = null;
        _rpcClearedWhileUnreachable = false;
        _presenceSessionKey = null;
        _presenceSessionStartUnix = 0;
        _titlesTask = _titles.LoadAsync(_cts.Token);

        Ui(() =>
        {
            IsRunning = true;
            LastError = string.Empty;
            StatusText = "Running";
            AppendLog("Client started.");
        });

        try
        {
            _ = _titlesTask.ContinueWith(
                t =>
                {
                    if (t.IsCompletedSuccessfully)
                    {
                        Ui(() => AppendLog($"Titles loaded: {_titles.PathOnDisk}"));
                    }
                    else if (t.IsFaulted)
                    {
                        Ui(() => AppendLog("Titles load failed (will show IDs)."));
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default
            );

            _ = UpdateTitleDbAsync(forceDownload: false);

            while (!_cts.IsCancellationRequested)
                {
                    await _discord.EnsureConnectedAsync(_cts.Token);

                    var state = await stateClient.FetchStateAsync(SwitchIp.Trim(), port, _cts.Token);
                    if (state is null)
                {
                    var nowUtc = DateTimeOffset.UtcNow;
                    _stateUnreachableSinceUtc ??= nowUtc;
                    var unreachableFor = nowUtc - _stateUnreachableSinceUtc.Value;

                    if (unreachableFor >= StateUnreachableClearDelay && !_rpcClearedWhileUnreachable)
                    {
                        if (_discord.IsConnected)
                        {
                            await _discord.ClearActivityAsync(_cts.Token);
                            _lastActivity = null;
                            _rpcClearedWhileUnreachable = true;
                            _presenceSessionKey = null;
                            _presenceSessionStartUnix = 0;
                            Ui(() => AppendLog("Switch endpoint unreachable for >=10s -> RPC cleared."));
                        }
                    }

                    Ui(() =>
                    {
                        StatusText = "Waiting for /state";
                        LastError = "Switch endpoint unreachable.";
                    });

                    await Task.Delay(pollMs, _cts.Token);
                        continue;
                    }

                    _stateUnreachableSinceUtc = null;
                    _rpcClearedWhileUnreachable = false;

                    var resolved = await ResolveGameAsync(state, _cts.Token);
                    Ui(() =>
                    {
                        LastError = string.Empty;
                        StatusText = _discord.IsConnected ? "Connected" : "Discord not connected";
                        CurrentGameText = $"Current game: {resolved.DisplayName}";
                    });

                    var activity = BuildActivity(state, resolved);
                    if (_discord.IsConnected && !activity.Equals(_lastActivity))
                    {
                        await _discord.SetActivityAsync(activity, _cts.Token);
                        _lastActivity = activity;
                    Ui(() => AppendLog($"RPC -> {activity.Details} | {activity.State}"));
                }

                await Task.Delay(pollMs, _cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on stop.
        }
        catch (Exception ex)
        {
            Ui(() =>
            {
                LastError = ex.Message;
                AppendLog($"Error: {ex.Message}");
            });
        }
        finally
        {
            try
            {
                if (_discord is not null)
                {
                    await _discord.ClearActivityAsync(CancellationToken.None);
                    await _discord.DisposeAsync();
                }
            }
            catch
            {
                // Ignore shutdown errors.
            }

            Ui(() =>
            {
                IsRunning = false;
                StatusText = "Stopped";
                AppendLog("Client stopped.");
            });
        }
    }

    private void Stop()
    {
        _cts?.Cancel();
    }

    private bool ValidateInputs(out int port, out int pollMs)
    {
        port = 6029;
        pollMs = 2000;

        if (string.IsNullOrWhiteSpace(SwitchIp))
        {
            LastError = "Switch IP is required.";
            return false;
        }

        if (!int.TryParse(Port, out port) || port <= 0 || port > 65535)
        {
            LastError = "Port must be between 1 and 65535.";
            return false;
        }

        if (!int.TryParse(PollIntervalMs, out pollMs) || pollMs < 250)
        {
            LastError = "Poll interval must be at least 250ms.";
            return false;
        }

        LastError = string.Empty;
        return true;
    }

    private ActivityPayload BuildActivity(SwitchState state, ResolvedTitle resolved)
    {
        var game = string.IsNullOrWhiteSpace(resolved.DisplayName) ? "Unknown" : resolved.DisplayName.Trim();
        var details = game.Equals("HOME", StringComparison.OrdinalIgnoreCase)
            ? "HOME-Menu"
            : $"{game}";
        var status = $"FW {Safe(state.Firmware)}";
        var batteryStatus = ShowBatteryStatus ? BuildBatteryStatus(state) : null;
        if (!string.IsNullOrWhiteSpace(batteryStatus))
        {
            status = $"{status} | {batteryStatus}";
        }
        var sessionKey = BuildPresenceSessionKey(state, game);
        if (!string.Equals(_presenceSessionKey, sessionKey, StringComparison.Ordinal))
        {
            _presenceSessionKey = sessionKey;
            _presenceSessionStartUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        var startUnix = _presenceSessionStartUnix > 0
            ? _presenceSessionStartUnix
            : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Discord RPC assets can be either app asset keys or (in some clients) external URLs.
        // We try the external icon URL from titledb. If it doesn't work on a given Discord build,
        // presence still works without images.
        var largeImage = string.IsNullOrWhiteSpace(resolved.IconUrl) ? null : resolved.IconUrl.Trim();
        var largeText = game;

        return new ActivityPayload(
            Clip(details, 128),
            Clip(status, 128),
            startUnix,
            Name: Clip(string.IsNullOrWhiteSpace(RpcName) ? DefaultRpcName : RpcName.Trim(), 128),
            LargeImage: largeImage,
            LargeText: Clip(largeText, 128),
            Button1Label: ShowGithubButton ? GithubButtonLabel : null,
            Button1Url: ShowGithubButton ? GithubRepoUrl : null
        );
    }

    private async Task<ResolvedTitle> ResolveGameAsync(SwitchState state, CancellationToken cancellationToken)
    {
        var game = string.IsNullOrWhiteSpace(state.ActiveGame) ? "Unknown" : state.ActiveGame.Trim();

        // If the Switch already provides a real name, keep it.
        // Some builds may send IDs without a "0x" prefix, so detect both formats.
        if (!LooksLikeTitleIdText(game))
        {
            return new ResolvedTitle(game, null);
        }

        // Prefer active_program_id as source of truth; fall back to active_game if needed.
        ulong tid;
        if (state.ActiveProgramId is not null && TryParseTitleId(state.ActiveProgramId, out var tidFromProgram))
        {
            tid = tidFromProgram;
        }
        else if (TryParseTitleId(game, out var tidFromGame))
        {
            tid = tidFromGame;
        }
        else
        {
            return new ResolvedTitle(game, null);
        }

        if (_titlesTask is not null)
        {
            try { await _titlesTask; } catch { /* ignore */ }
        }

        var (found, name) = await _titles.ResolveOrAddMissingAsync(tid, cancellationToken);
        if (!found)
        {
            if (tid != _lastMissingLoggedTid)
            {
                _lastMissingLoggedTid = tid;
                Ui(() => AppendLog($"New title seen: 0x{tid:X16} (added to Titles.txt)"));
            }
            return new ResolvedTitle(game, null);
        }

        string? iconUrl = null;
        if (_titledb.TryGetIconUrl(tid, out var u) && !string.IsNullOrWhiteSpace(u))
        {
            iconUrl = u;
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            return new ResolvedTitle(name, iconUrl);
        }

        // Fallback to titledb if user didn't provide a name yet.
        if (_titledb.TryResolve(tid, out var dbName) && !string.IsNullOrWhiteSpace(dbName))
        {
            return new ResolvedTitle(dbName, iconUrl);
        }

        return new ResolvedTitle(game, iconUrl);
    }

    private static bool LooksLikeTitleIdText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = text.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            s = s[2..];
        }

        // Switch title ids are 64-bit and are represented as exactly 16 hex digits.
        if (s.Length != 16)
        {
            return false;
        }

        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            var isHex =
                (c >= '0' && c <= '9') ||
                (c >= 'a' && c <= 'f') ||
                (c >= 'A' && c <= 'F');
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseTitleId(string text, out ulong titleId)
    {
        titleId = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = text.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            s = s[2..];
        }

        return ulong.TryParse(
            s,
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture,
            out titleId
        );
    }

    private void AppendLog(string line)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var next = $"[{timestamp}] {line}";

        if (string.IsNullOrEmpty(LogText))
        {
            LogText = next;
            return;
        }

        var sb = new StringBuilder(LogText);
        sb.AppendLine();
        sb.Append(next);
        LogText = sb.ToString();
    }

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max];

    private static string BuildPresenceSessionKey(SwitchState state, string gameName)
    {
        if (!string.IsNullOrWhiteSpace(state.ActiveProgramId))
        {
            return $"tid:{state.ActiveProgramId.Trim().ToUpperInvariant()}";
        }

        return $"name:{gameName.Trim().ToUpperInvariant()}";
    }

    private static string? BuildBatteryStatus(SwitchState state)
    {
        if (state.IsDocked == true)
        {
            return "Docked";
        }

        if (state.BatteryPercent is null && state.IsCharging is null)
        {
            return null;
        }

        if (state.BatteryPercent is null)
        {
            return state.IsCharging == true ? "Charging" : "On battery";
        }

        var pct = Math.Clamp(state.BatteryPercent.Value, 0, 100);
        if (state.IsCharging is null)
        {
            return $"BAT {pct}%";
        }

        return state.IsCharging == true
            ? $"BAT {pct}% charging"
            : $"BAT {pct}%";
    }

    private static void Ui(Action action)
    {
        if (System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            action();
            return;
        }

        System.Windows.Application.Current.Dispatcher.Invoke(action);
    }

    private Task UpdateTitleDbAsync(bool forceDownload)
    {
        var pack = string.IsNullOrWhiteSpace(SelectedTitleDbPack) ? DefaultTitleDbPack : SelectedTitleDbPack.Trim();
        var token = _cts?.Token ?? CancellationToken.None;

        Ui(() => AppendLog($"TitleDB update started: {pack} (forceDownload={forceDownload})"));

        _titledbTask = Task.Run(async () =>
        {
            try
            {
                var cfg = BuildCurrentConfig();
                cfg.TitleDbPack = pack;
                await _configStore.SaveAsync(cfg, token);
                await _titledb.LoadOrUpdateAsync(pack, forceDownload, token);
                Ui(() => AppendLog($"TitleDB ready: {pack} ({_titledb.EntryCount} entries)"));
            }
            catch (Exception ex)
            {
                Ui(() => AppendLog($"TitleDB update failed: {ex.Message}"));
            }
        });

        return _titledbTask;
    }

    private async Task LoadConfigAtStartupAsync()
    {
        try
        {
            var cfg = await _configStore.LoadAsync(CancellationToken.None);
            var pack = string.IsNullOrWhiteSpace(cfg.TitleDbPack) ? DefaultTitleDbPack : cfg.TitleDbPack.Trim();
            var rpcName = string.IsNullOrWhiteSpace(cfg.RpcName) ? DefaultRpcName : cfg.RpcName.Trim();
            var switchIp = string.IsNullOrWhiteSpace(cfg.SwitchIp) ? DefaultSwitchIp : cfg.SwitchIp.Trim();
            var port = string.IsNullOrWhiteSpace(cfg.Port) ? DefaultPort : cfg.Port.Trim();
            var poll = string.IsNullOrWhiteSpace(cfg.PollIntervalMs) ? DefaultPollIntervalMs : cfg.PollIntervalMs.Trim();
            var connectOnStartup = cfg.ConnectOnStartup;
            var startWithWindows = cfg.StartWithWindows;
            var showGithubButton = cfg.ShowGithubButton;
            var showBatteryStatus = cfg.ShowBatteryStatus;

            Ui(() =>
            {
                SwitchIp = switchIp;
                Port = port;
                PollIntervalMs = poll;
                RpcName = rpcName;
                ConnectOnStartup = connectOnStartup;
                StartWithWindows = startWithWindows;
                ShowGithubButton = showGithubButton;
                ShowBatteryStatus = showBatteryStatus;
                SelectedTitleDbPack = pack;
            });

            if (connectOnStartup)
            {
                Ui(Start);
            }
        }
        catch
        {
            // Keep in-memory defaults.
        }
    }

    private ClientConfig BuildCurrentConfig()
    {
        return new ClientConfig
        {
            TitleDbPack = string.IsNullOrWhiteSpace(SelectedTitleDbPack) ? DefaultTitleDbPack : SelectedTitleDbPack.Trim(),
            RpcName = string.IsNullOrWhiteSpace(RpcName) ? DefaultRpcName : RpcName.Trim(),
            SwitchIp = string.IsNullOrWhiteSpace(SwitchIp) ? DefaultSwitchIp : SwitchIp.Trim(),
            Port = string.IsNullOrWhiteSpace(Port) ? DefaultPort : Port.Trim(),
            PollIntervalMs = string.IsNullOrWhiteSpace(PollIntervalMs) ? DefaultPollIntervalMs : PollIntervalMs.Trim(),
            ConnectOnStartup = ConnectOnStartup,
            StartWithWindows = StartWithWindows,
            ShowGithubButton = ShowGithubButton,
            ShowBatteryStatus = ShowBatteryStatus
        };
    }
}
