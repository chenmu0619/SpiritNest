using System.Globalization;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpiritNest.Services;

/// <summary>
/// 省市区 catalog. Packed in the app and written to Assets\area.json beside the exe.
/// A newer remote file replaces that copy when <see cref="UpdateUrl"/> is set.
/// The chosen place stays in user settings.
/// </summary>
public static class AreaCatalog
{
    private const string ResourceName = "SpiritNest.Assets.area.json";
    private const string SkipLevel = "市辖区";

    /// <summary>Remote area.json. Same shape as the embedded file. Empty skips the check.</summary>
    public const string UpdateUrl = "";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly object Gate = new();
    private static IReadOnlyList<AreaNode>? _roots;

    public static void EnsureLocal()
    {
        RemoveUserDataCopy();
        RemoveBesideExeCopy();
        var embedded = ReadEmbedded();
        if (embedded.Length == 0 || !TryReadPack(embedded, out var embeddedVersion)) return;

        var path = AreaPath;
        var localBytes = File.Exists(path) ? File.ReadAllBytes(path) : null;
        decimal localVersion = 0;
        var localOk = localBytes is not null && TryReadPack(localBytes, out localVersion);
        var embeddedHasCoords = ContainsLocation(embedded);
        var localHasCoords = localBytes is not null && ContainsLocation(localBytes);
        if (!localOk || embeddedVersion > localVersion || (embeddedHasCoords && !localHasCoords))
            WriteAtomic(path, embedded);
    }

    public static async Task TryUpdateAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(UpdateUrl)) return;
        try
        {
            EnsureLocal();
            using var resp = await Http.GetAsync(UpdateUrl, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return;
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (!TryReadPack(bytes, out var remoteVersion)) return;
            var localVersion = File.Exists(AreaPath) && TryReadPack(File.ReadAllBytes(AreaPath), out var local)
                ? local
                : 0m;
            if (remoteVersion <= localVersion) return;
            WriteAtomic(AreaPath, bytes);
            lock (Gate) _roots = null;
        }
        catch
        {
            // Keep the extracted file when the host is unreachable.
        }
    }

    public static IReadOnlyList<WeatherPlace> Search(string query)
    {
        var q = query.Trim();
        if (q.Length == 0) return [];

        var hits = new List<WeatherPlace>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in Roots())
            Walk(root, [], q, hits, seen);
        return hits;
    }

    private static IReadOnlyList<AreaNode> Roots()
    {
        var cached = _roots;
        if (cached is not null) return cached;
        lock (Gate)
        {
            _roots ??= Load();
            return _roots;
        }
    }

    private static IReadOnlyList<AreaNode> Load()
    {
        EnsureLocal();
        if (File.Exists(AreaPath))
        {
            using var stream = File.OpenRead(AreaPath);
            return ReadData(stream);
        }

        var embedded = ReadEmbedded();
        if (embedded.Length == 0) return [];
        using var memory = new MemoryStream(embedded);
        return ReadData(memory);
    }

    private static IReadOnlyList<AreaNode> ReadData(Stream stream)
    {
        var file = JsonSerializer.Deserialize<AreaFile>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        return file?.Data ?? [];
    }

    private static string AreaPath => Path.Combine(InstallDirectory(), "Assets", "area.json");

    private static string InstallDirectory()
    {
        var exe = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(exe))
        {
            var dir = Path.GetDirectoryName(exe);
            if (!string.IsNullOrWhiteSpace(dir)) return dir;
        }

        return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static void RemoveBesideExeCopy()
    {
        try
        {
            var stray = Path.Combine(InstallDirectory(), "area.json");
            if (File.Exists(stray))
                File.Delete(stray);
        }
        catch
        {
            // An older build left this next to the exe.
        }
    }

    private static void RemoveUserDataCopy()
    {
        try
        {
            var stray = Path.Combine(AppPaths.Root, "area.json");
            if (File.Exists(stray))
                File.Delete(stray);
        }
        catch
        {
            // The catalog no longer reads this copy.
        }
    }

    public static bool TryGetCoordinates(string province, string city, string district, out double latitude, out double longitude)
    {
        latitude = 0;
        longitude = 0;
        foreach (var root in Roots())
        {
            if (!string.IsNullOrWhiteSpace(province) && root.Name != province) continue;
            var node = root;
            if (!string.IsNullOrWhiteSpace(city))
            {
                var next = FindNamed(node, city);
                if (next is null) continue;
                node = next;
            }
            if (!string.IsNullOrWhiteSpace(district))
            {
                var next = FindNamed(node, district);
                if (next is null) continue;
                node = next;
            }
            if (ReadNodeLocation(node, out latitude, out longitude)) return true;
        }
        return false;
    }

    private static AreaNode? FindNamed(AreaNode node, string name)
    {
        foreach (var child in node.Children)
        {
            if (child.Name == SkipLevel)
            {
                var nested = FindNamed(child, name);
                if (nested is not null) return nested;
                continue;
            }
            if (child.Name == name) return child;
        }
        foreach (var child in node.Children)
        {
            if (child.Name == SkipLevel || child.Name == name) continue;
            var nested = FindNamed(child, name);
            if (nested is not null) return nested;
        }
        return null;
    }

    private static bool ReadNodeLocation(AreaNode node, out double latitude, out double longitude)
    {
        if (TryLocation(node.Location, out latitude, out longitude)) return true;
        if (node.Geocodes is { Count: > 0 })
            return TryLocation(node.Geocodes[0].Location, out latitude, out longitude);
        latitude = 0;
        longitude = 0;
        return false;
    }

    private static bool ContainsLocation(byte[] bytes) =>
        bytes.AsSpan().IndexOf("\"location\""u8) >= 0;

    private static void Walk(
        AreaNode node,
        List<AreaNode> ancestors,
        string query,
        List<WeatherPlace> hits,
        HashSet<string> seen)
    {
        ancestors.Add(node);
        if (node.Name.Contains(query, StringComparison.Ordinal) && node.Name != SkipLevel)
            Emit(node, ancestors, hits, seen);
        else
        {
            foreach (var child in node.Children)
                Walk(child, ancestors, query, hits, seen);
        }
        ancestors.RemoveAt(ancestors.Count - 1);
    }

    private static void Emit(
        AreaNode node,
        List<AreaNode> chain,
        List<WeatherPlace> hits,
        HashSet<string> seen)
    {
        if (node.Children.Count == 0)
        {
            if (node.Name != SkipLevel)
                Add(chain, hits, seen);
            return;
        }

        foreach (var child in node.Children)
        {
            chain.Add(child);
            Emit(child, chain, hits, seen);
            chain.RemoveAt(chain.Count - 1);
        }
    }

    private static void Add(List<AreaNode> chain, List<WeatherPlace> hits, HashSet<string> seen)
    {
        var names = chain.Select(n => n.Name).Where(n => n != SkipLevel).ToArray();
        if (names.Length == 0) return;
        var leaf = chain[^1];
        if (leaf.Geocodes is { Count: > 1 })
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var geo in leaf.Geocodes)
                AddGeocode(names, geo, hits, seen, used);
            return;
        }

        var label = string.Join(" ", names);
        if (!seen.Add(label + "|" + leaf.Location)) return;
        TryLocation(leaf.Location, out var lat, out var lon);
        hits.Add(Place(names, lat, lon, ""));
    }

    private static void AddGeocode(
        string[] names,
        AreaGeocode geo,
        List<WeatherPlace> hits,
        HashSet<string> seen,
        HashSet<string> usedLabels)
    {
        if (!TryLocation(geo.Location, out var lat, out var lon)) return;
        var province = First(geo.Province, names.Length > 0 ? names[0] : "");
        var city = First(geo.City, names.Length >= 2 ? names[1] : "");
        var district = First(geo.District, names.Length >= 3 ? string.Join(" ", names.Skip(2)) : "");
        var parts = new[] { province, city, district };
        var label = string.Join(" ", parts.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal));
        var display = "";
        if (!usedLabels.Add(label))
            display = string.IsNullOrWhiteSpace(geo.FormattedAddress) ? label + " " + geo.Level : geo.FormattedAddress;
        if (!seen.Add((string.IsNullOrWhiteSpace(display) ? label : display) + "|" + geo.Location)) return;
        hits.Add(new WeatherPlace
        {
            Province = province,
            City = city,
            District = district,
            Latitude = lat,
            Longitude = lon,
            Display = display
        });
    }

    private static WeatherPlace Place(string[] names, double lat, double lon, string display) => new()
    {
        Province = names[0],
        City = names.Length >= 2 ? names[1] : "",
        District = names.Length >= 3 ? string.Join(" ", names.Skip(2)) : "",
        Latitude = lat,
        Longitude = lon,
        Display = display
    };

    private static string First(string? preferred, string fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;

    private static bool TryLocation(string? text, out double lat, out double lon)
    {
        lat = 0;
        lon = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split(',');
        if (parts.Length != 2) return false;
        return double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out lon)
               && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out lat)
               && (lon != 0 || lat != 0);
    }

    private static byte[] ReadEmbedded()
    {
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (stream is null) return [];
        using (stream)
        {
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }
    }

    private static bool TryReadPack(byte[] bytes, out decimal version)
    {
        version = 0;
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return false;
            if (!root.TryGetProperty("version", out var versionEl)) return false;
            return versionEl.TryGetDecimal(out version);
        }
        catch
        {
            return false;
        }
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // The install directory may be read-only. Search still uses the packed copy.
        }
    }

    private sealed class AreaFile
    {
        public List<AreaNode> Data { get; set; } = [];
    }

    private sealed class AreaNode
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Location { get; set; } = "";
        public List<AreaGeocode> Geocodes { get; set; } = [];
        public List<AreaNode> Children { get; set; } = [];
    }

    private sealed class AreaGeocode
    {
        [JsonPropertyName("formatted_address")]
        public string FormattedAddress { get; set; } = "";
        public string Province { get; set; } = "";
        public string City { get; set; } = "";
        public string District { get; set; } = "";
        public string Adcode { get; set; } = "";
        public string Location { get; set; } = "";
        public string Level { get; set; } = "";
    }
}
