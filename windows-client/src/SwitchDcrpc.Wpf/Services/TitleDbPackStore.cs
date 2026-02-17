using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace SwitchDcrpc.Wpf.Services;

public sealed class TitleDbPackStore
{
    private static readonly string[] DefaultPacks =
    [
        "DE.de.json",
        "US.en.json",
        "EU.en.json",
        "JP.ja.json",
        "FR.fr.json",
        "ES.es.json",
        "IT.it.json",
        "PT.pt.json",
        "RU.ru.json",
        "KO.ko.json",
        "ZH.zh.json"
    ];

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<ulong, TitleDbEntry>? _map;

    public sealed record TitleDbEntry(string Name, string? IconUrl);

    public TitleDbPackStore()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("RichNX/1.0 (+titledb)");
    }

    public IReadOnlyList<string> Packs => DefaultPacks;

    public bool IsLoaded => _map is not null;

    public int EntryCount => _map?.Count ?? 0;

    public string DbDir => Path.Combine(AppContext.BaseDirectory, "DB", "titledb");

    public async Task LoadOrUpdateAsync(string packFile, bool forceDownload, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(packFile))
        {
            packFile = "DE.de.json";
        }

        packFile = packFile.Trim();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(DbDir);

            var rawPath = Path.Combine(DbDir, packFile);
            var mapPath = Path.Combine(DbDir, packFile + ".map.json");

            // Prefer compiled map for fast startup and to allow deleting the raw pack after compilation.
            if (!forceDownload && File.Exists(mapPath))
            {
                var compiled = await File.ReadAllBytesAsync(mapPath, cancellationToken);
                _map = ParseCompiledMap(compiled);
                return;
            }

            // Need raw to (re-)build the compiled map.
            if (forceDownload || !File.Exists(rawPath))
            {
                var bytes = await DownloadPackAsync(packFile, cancellationToken);
                await File.WriteAllBytesAsync(rawPath, bytes, cancellationToken);
            }

            // Prefer compiled map for fast startup when it's already newer than raw.
            if (File.Exists(mapPath) && File.Exists(rawPath) &&
                File.GetLastWriteTimeUtc(mapPath) >= File.GetLastWriteTimeUtc(rawPath))
            {
                var compiled = await File.ReadAllBytesAsync(mapPath, cancellationToken);
                _map = ParseCompiledMap(compiled);
                return;
            }

            var raw = await File.ReadAllBytesAsync(rawPath, cancellationToken);
            _map = ParseRawPack(raw);

            var compiledBytes = BuildCompiledMapJson(_map);
            await File.WriteAllBytesAsync(mapPath, compiledBytes, cancellationToken);

            // Keep disk usage low; raw can always be re-downloaded.
            try { File.Delete(rawPath); } catch { /* ignore */ }
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool TryResolve(ulong titleId, out string name)
    {
        name = string.Empty;
        if (_map is null)
        {
            return false;
        }

        if (!_map.TryGetValue(titleId, out var entry))
        {
            return false;
        }

        name = entry.Name;
        return true;
    }

    public bool TryGetIconUrl(ulong titleId, out string? iconUrl)
    {
        iconUrl = null;
        if (_map is null)
        {
            return false;
        }

        if (!_map.TryGetValue(titleId, out var entry))
        {
            return false;
        }

        iconUrl = entry.IconUrl;
        return true;
    }

    private async Task<byte[]> DownloadPackAsync(string packFile, CancellationToken cancellationToken)
    {
        // Mirrors; some networks block raw.githubusercontent.com.
        var urls = new[]
        {
            $"https://cdn.jsdelivr.net/gh/blawar/titledb@master/{packFile}",
            $"https://raw.githubusercontent.com/blawar/titledb/master/{packFile}",
            $"https://github.com/blawar/titledb/raw/master/{packFile}"
        };

        Exception? last = null;
        foreach (var url in urls)
        {
            try
            {
                return await _http.GetByteArrayAsync(url, cancellationToken);
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw last ?? new InvalidOperationException("Failed to download titledb pack.");
    }

    private static byte[] BuildCompiledMapJson(Dictionary<ulong, TitleDbEntry> map)
    {
        // Store as object: { "0100...": { "name": "...", "iconUrl": "https://..." }, ... }
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });
        writer.WriteStartObject();
        foreach (var kv in map.OrderBy(k => k.Key))
        {
            writer.WritePropertyName(kv.Key.ToString("X16", CultureInfo.InvariantCulture));
            writer.WriteStartObject();
            writer.WriteString("name", kv.Value.Name);
            if (!string.IsNullOrWhiteSpace(kv.Value.IconUrl))
            {
                writer.WriteString("iconUrl", kv.Value.IconUrl);
            }
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
        writer.Flush();
        return stream.ToArray();
    }

    private static Dictionary<ulong, TitleDbEntry> ParseCompiledMap(byte[] jsonBytes)
    {
        var map = new Dictionary<ulong, TitleDbEntry>();
        try
        {
            using var doc = JsonDocument.Parse(jsonBytes);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return map;
            }

            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (!TryParseTitleId(p.Name, out var tid))
                {
                    continue;
                }

                // Backward compatible: old format was { "0100...": "Name" }
                if (p.Value.ValueKind == JsonValueKind.String)
                {
                    var n = p.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(n))
                    {
                        map[tid] = new TitleDbEntry(n.Trim(), null);
                    }
                    continue;
                }

                if (p.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var name = GetString(p.Value, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var iconUrl = GetString(p.Value, "iconUrl");
                map[tid] = new TitleDbEntry(name.Trim(), string.IsNullOrWhiteSpace(iconUrl) ? null : iconUrl.Trim());
            }
        }
        catch
        {
            // ignore
        }
        return map;
    }

    private static Dictionary<ulong, TitleDbEntry> ParseRawPack(byte[] jsonBytes)
    {
        // Titledb packs can be arrays or objects; objects are often keyed by nsuId.
        // We scan for any object that contains a title id and a name (and optionally iconUrl).
        var map = new Dictionary<ulong, TitleDbEntry>();
        try
        {
            using var doc = JsonDocument.Parse(jsonBytes);
            Scan(doc.RootElement, map);
        }
        catch
        {
            // ignore
        }
        return map;
    }

    private static void Scan(JsonElement el, Dictionary<ulong, TitleDbEntry> map)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                {
                    Scan(item, map);
                }
                return;
            case JsonValueKind.Object:
            {
                // Key-as-titleid map
                foreach (var prop in el.EnumerateObject())
                {
                    if (TryParseTitleId(prop.Name, out var tidFromKey))
                    {
                        var entryFromKeyObj = ExtractEntry(prop.Value);
                        if (entryFromKeyObj is not null && !map.ContainsKey(tidFromKey))
                        {
                            map[tidFromKey] = entryFromKeyObj;
                        }
                    }
                }

                var tidText = ExtractTitleId(el);
                var entry = ExtractEntry(el);
                if (TryParseTitleId(tidText ?? string.Empty, out var tid) &&
                    entry is not null &&
                    !map.ContainsKey(tid))
                {
                    map[tid] = entry;
                }

                foreach (var p in el.EnumerateObject())
                {
                    if (p.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                    {
                        Scan(p.Value, map);
                    }
                }
                return;
            }
            default:
                return;
        }
    }

    private static TitleDbEntry? ExtractEntry(JsonElement obj)
    {
        var name = ExtractName(obj);
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var icon = ExtractIconUrl(obj);
        return new TitleDbEntry(name, icon);
    }

    private static string? ExtractTitleId(JsonElement obj)
    {
        // Common fields in the wild.
        return GetString(obj, "titleId")
               ?? GetString(obj, "title_id")
               ?? GetString(obj, "titleid")
               ?? GetString(obj, "tid")
                ?? GetString(obj, "id");
    }

    private static string? ExtractName(JsonElement obj)
    {
        var n = GetString(obj, "name")
                ?? GetString(obj, "title")
                ?? GetString(obj, "game")
                ?? GetString(obj, "Name");
        return string.IsNullOrWhiteSpace(n) ? null : n.Trim();
    }

    private static string? ExtractIconUrl(JsonElement obj)
    {
        var u = GetString(obj, "iconUrl")
                ?? GetString(obj, "icon_url")
                ?? GetString(obj, "icon")
                ?? GetString(obj, "IconUrl");

        // Some packs also carry box art; use it if it's the only available image.
        u ??= GetString(obj, "frontBoxArt");
        u ??= GetString(obj, "bannerUrl");

        return string.IsNullOrWhiteSpace(u) ? null : u.Trim();
    }

    private static string? GetString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el))
        {
            return null;
        }
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
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
