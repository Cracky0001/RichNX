using System.Globalization;
using System.IO;
using System.Text;

namespace SwitchDcrpc.Wpf.Services;

// Simple local mapping: one line per title id.
// Format: <hexTitleId>:<optionalName>
public sealed class TitleListStore
{
    private readonly string _path;
    private readonly string _legacyPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<ulong, string> _map = new();
    private DateTime _lastLoadedUtc;
    private DateTime _lastSeenWriteUtc;

    public TitleListStore()
    {
        _path = Path.Combine(
            AppContext.BaseDirectory,
            "DB",
            "Titles.txt"
        );

        // Older builds stored the file in LocalAppData. Migrate once if present.
        _legacyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SwitchDCActivity",
            "Titles.txt"
        );
    }

    public string PathOnDisk => _path;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _map.Clear();
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            if (!File.Exists(_path) && File.Exists(_legacyPath))
            {
                try
                {
                    File.Copy(_legacyPath, _path, overwrite: true);
                }
                catch
                {
                    // Ignore migration failures; we'll just create a new file.
                }
            }

            if (!File.Exists(_path))
            {
                await File.WriteAllTextAsync(_path, "# TitleID:Name\n", Encoding.UTF8, cancellationToken);
                _lastSeenWriteUtc = File.GetLastWriteTimeUtc(_path);
                _lastLoadedUtc = DateTime.UtcNow;
                return;
            }

            _lastSeenWriteUtc = File.GetLastWriteTimeUtc(_path);
            var lines = await File.ReadAllLinesAsync(_path, Encoding.UTF8, cancellationToken);
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                var idx = line.IndexOf(':');
                if (idx <= 0)
                {
                    continue;
                }

                var idText = line[..idx].Trim();
                var name = line[(idx + 1)..].Trim();
                if (!TryParseTitleId(idText, out var tid))
                {
                    continue;
                }

                if (!_map.ContainsKey(tid))
                {
                    _map[tid] = name;
                }
            }
            _lastLoadedUtc = DateTime.UtcNow;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<(bool found, string name)> ResolveOrAddMissingAsync(ulong titleId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Pick up manual edits without needing a restart.
            if (File.Exists(_path))
            {
                var write = File.GetLastWriteTimeUtc(_path);
                if (write > _lastSeenWriteUtc && (DateTime.UtcNow - _lastLoadedUtc) > TimeSpan.FromMilliseconds(250))
                {
                    _gate.Release();
                    try
                    {
                        await LoadAsync(cancellationToken);
                    }
                    finally
                    {
                        await _gate.WaitAsync(cancellationToken);
                    }
                }
            }

            if (_map.TryGetValue(titleId, out var existing))
            {
                // "found" means the TitleID exists in the list (even if the user hasn't filled the name yet).
                // An empty name should still allow higher-level fallbacks (e.g. titledb) to run.
                return (true, existing);
            }

            _map[titleId] = string.Empty;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await File.AppendAllTextAsync(_path, $"{titleId:X16}:\n", Encoding.UTF8, cancellationToken);
            _lastSeenWriteUtc = File.GetLastWriteTimeUtc(_path);
            return (false, string.Empty);
        }
        finally
        {
            _gate.Release();
        }
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

        return ulong.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out titleId);
    }
}
