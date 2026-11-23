using System.Net.Http;
using System.Text.Json;

namespace SpiritNest.Services;

public sealed class WeatherSnapshot
{
    public string City { get; init; } = "";
    public double Temperature { get; init; }
    public double TempMin { get; init; }
    public double TempMax { get; init; }
    public int WeatherCode { get; init; }
    public string WeatherText { get; init; } = "";
    public string WeatherGlyph { get; init; } = WeatherService.FallbackGlyph;
    public IReadOnlyList<DailyForecast> Days { get; init; } = [];
}

public sealed class WeatherPlace
{
    public string Province { get; init; } = "";
    public string City { get; init; } = "";
    public string District { get; init; } = "";
    public double Latitude { get; init; }
    public double Longitude { get; init; }

    /// <summary>Shown when one place name matches more than one coordinate.</summary>
    public string Display { get; init; } = "";

    public string Label
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Display)) return Display;
            var parts = new[] { Province, City, District }
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return parts.Length == 0 ? "" : string.Join(" ", parts);
        }
    }

    /// <summary>Most specific level: district, otherwise city, otherwise province.</summary>
    public string Leaf
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(District)) return District;
            if (!string.IsNullOrWhiteSpace(City)) return City;
            return Province;
        }
    }
}

public sealed class DailyForecast
{
    public DateTime Date { get; init; }
    public int WeatherCode { get; init; }
    public string WeatherText { get; init; } = "";
    public string WeatherGlyph { get; init; } = WeatherService.FallbackGlyph;
    /// <summary>Segoe MDL2 / Fluent weather symbol for reliable WPF rendering.</summary>
    public string WeatherIcon { get; init; } = "\uE753";
    public double TempMin { get; init; }
    public double TempMax { get; init; }
    public string Weekday => "日一二三四五六"[(int)Date.DayOfWeek].ToString();
    /// <summary>e.g. 三 5 — safe for TextBlock binding (avoid Run+StringFormat).</summary>
    public string DayLabel => $"{Weekday} {Date.Day}";
    public string TempLabel => $"{TempMax:0}° / {TempMin:0}°";
}

public static class WeatherService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static WeatherSnapshot? _cache;
    private static string? _cacheKey;
    private static DateTime _cacheAt = DateTime.MinValue;

    /// <summary>Local time of the forecast currently shown. Unchanged when a later call hits the cache.</summary>
    public static DateTime? ForecastUpdatedLocal =>
        _cacheAt == DateTime.MinValue ? null : _cacheAt.ToLocalTime();

    private static WeatherPlace? _locateCache;
    private static DateTime _locateAt = DateTime.MinValue;

    /// <summary>Supplier that produced the current automatic location: 高德, IPinfo, or ip-api.</summary>
    public static string? LocationSource { get; private set; }
    private const string NominatimAgent = "SpiritPerch/0.1.0 (desktop weather)";

    /// <summary>
    /// Auto location. A configured 高德 key is tried first.
    /// Otherwise, or if 高德 fails, use the public IP databases.
    /// </summary>
    public static async Task<WeatherPlace> LocateAsync(string? amapKey, bool force = false, CancellationToken ct = default)
    {
        if (!force && _locateCache is not null && DateTime.UtcNow - _locateAt < TimeSpan.FromMinutes(20))
            return _locateCache;

        WeatherPlace? place = null;
        if (!string.IsNullOrWhiteSpace(amapKey))
        {
            place = await TryAmapAsync(amapKey.Trim(), ct).ConfigureAwait(false);
            if (place is not null)
                LocationSource = "高德";
        }
        place ??= await LookupIpPlaceAsync(ct).ConfigureAwait(false);
        if (place is null)
            throw new InvalidOperationException("定位失败");

        _locateCache = place;
        _locateAt = DateTime.UtcNow;
        return place;
    }

    /// <summary>status 1: province and city, then local coordinates. status 0: info is the error.</summary>
    private static async Task<WeatherPlace?> TryAmapAsync(string amapKey, CancellationToken ct)
    {
        try
        {
            var ip = await PublicIpv4Async(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(ip)) return null;
            var url = "https://restapi.amap.com/v3/ip?ip="
                      + Uri.EscapeDataString(ip)
                      + "&key=" + Uri.EscapeDataString(amapKey);
            using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = doc.RootElement;
            if (!AmapSucceeded(root)) return null;
            var province = AmapText(root, "province");
            var city = AmapText(root, "city");
            if (string.IsNullOrWhiteSpace(province) && string.IsNullOrWhiteSpace(city)) return null;
            if (!AreaCatalog.TryGetCoordinates(province, city, "", out var latitude, out var longitude))
                return null;
            return new WeatherPlace
            {
                Province = province,
                City = city,
                Latitude = latitude,
                Longitude = longitude
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>status is "1" on success and "0" on failure. info is "OK", or the error reason.</summary>
    private static bool AmapSucceeded(JsonElement root)
    {
        if (!root.TryGetProperty("status", out var status)) return false;
        var ok = status.ValueKind switch
        {
            JsonValueKind.String => status.GetString() == "1",
            JsonValueKind.Number => status.TryGetInt32(out var code) && code == 1,
            _ => false
        };
        return ok;
    }

    /// <summary>高德把缺失的 city 回成 []，不当成地名。</summary>
    private static string AmapText(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return "";
        return value.GetString()?.Trim() ?? "";
    }

    private static async Task<string?> PublicIpv4Async(CancellationToken ct)
    {
        foreach (var url in new[] { "https://api.ipify.org", "https://ipinfo.io/ip" })
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(4));
                var ip = (await Http.GetStringAsync(url, timeout.Token).ConfigureAwait(false)).Trim();
                if (System.Net.IPAddress.TryParse(ip, out var addr)
                    && addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    return ip;
            }
            catch
            {
                // Try the next echo service.
            }
        }
        return null;
    }

    private readonly record struct IpHit(double Latitude, double Longitude, string City);

    private static async Task<WeatherPlace?> LookupIpPlaceAsync(CancellationToken ct)
    {
        double lat = 39.9, lon = 116.4;
        var city = "北京";
        var hit = await LookupIpAsync(ct).ConfigureAwait(false);
        if (hit is not null)
        {
            lat = hit.Value.Latitude;
            lon = hit.Value.Longitude;
            if (!string.IsNullOrWhiteSpace(hit.Value.City))
                city = hit.Value.City;
        }

        var reversed = await ReverseAsync(lat, lon, ct).ConfigureAwait(false);
        if (reversed is not null && !string.IsNullOrWhiteSpace(reversed.Label))
            return reversed;

        var zh = await ChineseCityName(city, lat, lon, ct).ConfigureAwait(false);
        return new WeatherPlace
        {
            City = string.IsNullOrWhiteSpace(zh) ? city : zh,
            Latitude = lat,
            Longitude = lon
        };
    }

    private static async Task<IpHit?> LookupIpAsync(CancellationToken ct)
    {
        var info = await TryIpInfoAsync(ct).ConfigureAwait(false);
        if (info is not null)
        {
            LocationSource = "IPinfo";
            return info;
        }
        var api = await TryIpApiAsync(ct).ConfigureAwait(false);
        if (api is not null)
            LocationSource = "ip-api";
        return api;
    }

    private static async Task<IpHit?> TryIpInfoAsync(CancellationToken ct)
    {
        try
        {
            var json = await GetIpJsonAsync("https://ipinfo.io/json", ct).ConfigureAwait(false);
            if (json is null) return null;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("loc", out var locEl)) return null;
            var loc = locEl.GetString()?.Split(',');
            if (loc is not { Length: 2 }) return null;
            if (!double.TryParse(loc[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lat))
                return null;
            if (!double.TryParse(loc[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lon))
                return null;
            if (lat == 0 && lon == 0) return null;
            var city = root.TryGetProperty("city", out var cityEl) ? cityEl.GetString() ?? "" : "";
            return new IpHit(lat, lon, city);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<IpHit?> TryIpApiAsync(CancellationToken ct)
    {
        try
        {
            var json = await GetIpJsonAsync("http://ip-api.com/json/?fields=status,city,lat,lon", ct).ConfigureAwait(false);
            if (json is null) return null;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("status", out var st) || st.GetString() != "success")
                return null;
            var lat = root.GetProperty("lat").GetDouble();
            var lon = root.GetProperty("lon").GetDouble();
            if (lat == 0 && lon == 0) return null;
            var city = root.TryGetProperty("city", out var cityEl) ? cityEl.GetString() ?? "" : "";
            return new IpHit(lat, lon, city);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> GetIpJsonAsync(string url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        using var resp = await Http.GetAsync(url, timeout.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
    }

    private static async Task<WeatherPlace?> ReverseAsync(double lat, double lon, CancellationToken ct)
    {
        try
        {
            var url = "https://nominatim.openstreetmap.org/reverse"
                      + $"?lat={lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                      + $"&lon={lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                      + "&format=jsonv2&addressdetails=1&accept-language=zh-CN&zoom=12";
            var json = await NominatimGet(url, ct).ConfigureAwait(false);
            if (json is null) return null;
            using var doc = JsonDocument.Parse(json);
            return ReadPlace(doc.RootElement, lat, lon);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> ChineseCityName(string englishCity, double lat, double lon, CancellationToken ct)
    {
        try
        {
            var url = "https://geocoding-api.open-meteo.com/v1/search?name="
                      + Uri.EscapeDataString(englishCity)
                      + "&count=10&language=zh&format=json";
            using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("results", out var results)) return null;

            string? best = null;
            var bestKm = double.MaxValue;
            foreach (var item in results.EnumerateArray())
            {
                if (!item.TryGetProperty("name", out var nameEl)) continue;
                var name = nameEl.GetString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                var itemLat = item.GetProperty("latitude").GetDouble();
                var itemLon = item.GetProperty("longitude").GetDouble();
                var km = DistanceKm(lat, lon, itemLat, itemLon);
                if (km < bestKm)
                {
                    bestKm = km;
                    best = name;
                }
            }

            if (best is null || bestKm > 150) return null;
            return best;
        }
        catch
        {
            return null;
        }
    }

    private static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double radius = 6371;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180)
                * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return radius * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    public static async Task<IReadOnlyList<WeatherPlace>> SearchAsync(string query, CancellationToken ct = default)
    {
        var q = query.Trim();
        if (q.Length == 0) return [];
        var list = await SearchNominatim(q, restrictChina: true, ct).ConfigureAwait(false);
        if (list.Count == 0)
            list = await SearchNominatim(q, restrictChina: false, ct).ConfigureAwait(false);
        return list;
    }

    public static async Task<WeatherSnapshot> ForecastAsync(
        WeatherPlace place, bool force = false, CancellationToken ct = default)
    {
        var cacheKey = $"{place.Latitude:0.####},{place.Longitude:0.####}";
        if (!force && _cache is not null && _cacheKey == cacheKey && DateTime.UtcNow - _cacheAt < TimeSpan.FromMinutes(20))
            return _cache;

        var lat = place.Latitude;
        var lon = place.Longitude;
        var city = string.IsNullOrWhiteSpace(place.Leaf) ? "北京" : place.Leaf;
        var url =
            $"https://api.open-meteo.com/v1/forecast?latitude={lat:0.####}&longitude={lon:0.####}" +
            "&current=temperature_2m,weather_code,is_day" +
            "&daily=weather_code,temperature_2m_max,temperature_2m_min" +
            "&timezone=auto&forecast_days=7";

        using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var weatherDoc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var w = weatherDoc.RootElement;
        var current = w.GetProperty("current");
        var temp = current.GetProperty("temperature_2m").GetDouble();
        var code = current.GetProperty("weather_code").GetInt32();
        var day = !current.TryGetProperty("is_day", out var isDay) || isDay.GetInt32() != 0;

        var daily = w.GetProperty("daily");
        var times = daily.GetProperty("time").EnumerateArray().Select(x => DateTime.Parse(x.GetString()!)).ToList();
        var codes = daily.GetProperty("weather_code").EnumerateArray().Select(x => x.GetInt32()).ToList();
        var maxs = daily.GetProperty("temperature_2m_max").EnumerateArray().Select(x => x.GetDouble()).ToList();
        var mins = daily.GetProperty("temperature_2m_min").EnumerateArray().Select(x => x.GetDouble()).ToList();

        var days = new List<DailyForecast>();
        for (var i = 0; i < times.Count; i++)
        {
            var (text, glyph, icon) = Describe(codes[i]);
            days.Add(new DailyForecast
            {
                Date = times[i],
                WeatherCode = codes[i],
                WeatherText = text,
                WeatherGlyph = glyph,
                WeatherIcon = icon,
                TempMin = mins[i],
                TempMax = maxs[i]
            });
        }

        var (curText, curGlyph, _) = Describe(code, day);
        _cache = new WeatherSnapshot
        {
            City = city,
            Temperature = temp,
            TempMin = days.Count > 0 ? days[0].TempMin : temp,
            TempMax = days.Count > 0 ? days[0].TempMax : temp,
            WeatherCode = code,
            WeatherText = curText,
            WeatherGlyph = curGlyph,
            Days = days
        };
        _cacheAt = DateTime.UtcNow;
        _cacheKey = cacheKey;
        return _cache;
    }

    private static async Task<List<WeatherPlace>> SearchNominatim(string query, bool restrictChina, CancellationToken ct)
    {
        var list = new List<WeatherPlace>();
        try
        {
            var url = "https://nominatim.openstreetmap.org/search?q="
                      + Uri.EscapeDataString(query)
                      + "&format=jsonv2&addressdetails=1&accept-language=zh-CN&limit=8";
            if (restrictChina) url += "&countrycodes=cn";
            var json = await NominatimGet(url, ct).ConfigureAwait(false);
            if (json is null) return list;
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var place = ReadPlace(item, fallbackLat: null, fallbackLon: null);
                if (place is null || string.IsNullOrWhiteSpace(place.Label)) continue;
                if (!seen.Add(place.Label)) continue;
                list.Add(place);
            }
        }
        catch { /* empty list */ }
        return list;
    }

    private static async Task<string?> NominatimGet(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", NominatimAgent);
        req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN");
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private static WeatherPlace? ReadPlace(JsonElement item, double? fallbackLat, double? fallbackLon)
    {
        double lat, lon;
        if (item.TryGetProperty("lat", out var latEl) && item.TryGetProperty("lon", out var lonEl)
            && double.TryParse(latEl.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out lat)
            && double.TryParse(lonEl.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out lon))
        {
            /* nominatim returns lat/lon as strings */
        }
        else if (fallbackLat is not null && fallbackLon is not null)
        {
            lat = fallbackLat.Value;
            lon = fallbackLon.Value;
        }
        else
            return null;

        if (!item.TryGetProperty("address", out var address) || address.ValueKind != JsonValueKind.Object)
            return null;
        return ParseAddress(address, lat, lon);
    }

    private static WeatherPlace ParseAddress(JsonElement address, double lat, double lon)
    {
        string G(string key) =>
            address.TryGetProperty(key, out var value) ? value.GetString()?.Trim() ?? "" : "";

        var state = G("state");
        var region = G("region");
        var city = G("city");
        var town = G("town");
        var county = G("county");
        var district = G("city_district");
        if (district.Length == 0) district = G("suburb");
        if (district.Length == 0) district = county;

        var province = state;
        var shi = "";
        var qu = "";
        if (IsShi(region) && region != state) shi = region;
        if (IsShi(city) && shi.Length == 0) shi = city;
        if (IsQu(city)) qu = city;
        if (qu.Length == 0 && IsQu(district)) qu = district;
        if (qu.Length == 0 && IsQu(town)) qu = town;
        if (qu.Length == 0 && IsQu(county)) qu = county;
        if (shi.Length == 0 && IsShi(region)) shi = region;
        if (qu == shi) qu = "";
        if (shi == province) shi = "";

        return new WeatherPlace
        {
            Province = province,
            City = shi,
            District = qu,
            Latitude = lat,
            Longitude = lon
        };
    }

    private static bool IsShi(string s) => s.EndsWith('市');

    private static bool IsQu(string s) => s.EndsWith('区') || s.EndsWith('县') || s.EndsWith('旗');

    /// <summary>QWeather Icons (CC BY 4.0). Glyphs are private-use characters in qweather-icons.ttf.</summary>
    public const string FallbackGlyph = "\uf102";

    public static (string Text, string Glyph, string Icon) Describe(int code, bool day = true)
    {
        var (text, iconId) = code switch
        {
            0 => ("晴", 100),
            1 => ("少云", 102),
            2 => ("少云", 103),
            3 => ("阴", 104),
            45 or 48 => ("雾", 501),
            51 or 53 or 55 => ("雨", 309),
            56 or 57 or 66 or 67 => ("雨", 313),
            61 => ("雨", 305),
            63 => ("雨", 306),
            65 => ("雨", 307),
            71 or 77 => ("雪", 400),
            73 => ("雪", 401),
            75 => ("雪", 402),
            80 => ("阵雨", 300),
            81 or 82 => ("阵雨", 301),
            85 or 86 => ("阵雪", 407),
            95 => ("雷雨", 302),
            >= 96 => ("雷雨", 304),
            _ => ("多云", 101)
        };
        if (!day)
        {
            iconId = iconId switch
            {
                100 => 150,
                101 => 151,
                102 => 152,
                103 => 153,
                _ => iconId
            };
        }

        var glyph = IconChar(iconId);
        return (text, glyph, glyph);
    }

    private static string IconChar(int iconId) => iconId switch
    {
        100 => "\uf101",
        101 => "\uf102",
        102 => "\uf103",
        103 => "\uf104",
        104 => "\uf105",
        150 => "\uf106",
        151 => "\uf107",
        152 => "\uf108",
        153 => "\uf109",
        300 => "\uf10a",
        301 => "\uf10b",
        302 => "\uf10c",
        304 => "\uf10e",
        305 => "\uf10f",
        306 => "\uf110",
        307 => "\uf111",
        309 => "\uf113",
        313 => "\uf117",
        400 => "\uf120",
        401 => "\uf121",
        402 => "\uf122",
        407 => "\uf127",
        501 => "\uf12f",
        _ => FallbackGlyph
    };
}
