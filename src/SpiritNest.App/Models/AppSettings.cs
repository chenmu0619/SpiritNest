namespace SpiritNest.Models;

public sealed class AppSettings
{
    /// <summary>Hover dwell before opening the panel.</summary>
    public int ExpandDelayMs { get; set; } = 240;
    /// <summary>Delay after leaving ball∪panel before closing an unpinned panel.</summary>
    public int CollapseDelayMs { get; set; } = 200;
    /// <summary>FollowSystem | Light | Dark.</summary>
    public string ThemeMode { get; set; } = "FollowSystem";
    /// <summary>Accent hex, e.g. #4F9DFF.</summary>
    public string AccentColor { get; set; } = "#4F9DFF";
    /// <summary>Settings schema (8 = animation always on).</summary>
    public int SettingsSchema { get; set; } = 8;
    public bool StartWithWindows { get; set; }
    public bool HideOnFullscreen { get; set; } = true;
    /// <summary>Panel open/close transition animation.</summary>
    public bool EnablePanelAnimation { get; set; } = true;

    /// <summary>Expand / view-switch animation length, 100–2000 ms in 50 ms steps.</summary>
    public int AnimationDurationMs { get; set; } = 360;

    /// <summary>Collapsed UI is a thin top line instead of the full notch.</summary>
    public bool MinimalNotch { get; set; }

    /// <summary>While a browser window is maximized, collapse the island to the thin line.</summary>
    public bool BrowserMinimal { get; set; }

    /// <summary>Idle seconds on search/weather subviews before returning to icons (default 10).</summary>
    public int SearchWeatherIdleSeconds { get; set; } = 10;

    /// <summary>Optional browser exe path for web search; empty = system default.</summary>
    public string? PreferredBrowserPath { get; set; }

    /// <summary>When false, the panel hides the weather button and the notch hides temperature and conditions.</summary>
    public bool ShowWeather { get; set; } = true;

    /// <summary>auto = IP location, 高德 when <see cref="AmapKey"/> is set. manual = the saved place.</summary>
    public string WeatherCitySource { get; set; } = "auto";

    /// <summary>高德 Web 服务 Key。自动定位且非空时优先用它定位。</summary>
    public string AmapKey { get; set; } = "";

    /// <summary>City-level name, such as 南昌市.</summary>
    public string WeatherCity { get; set; } = "";

    public string WeatherProvince { get; set; } = "";

    public string WeatherDistrict { get; set; } = "";

    public double WeatherLatitude { get; set; }

    public double WeatherLongitude { get; set; }

    public bool HasWeatherPlace => WeatherLatitude != 0 || WeatherLongitude != 0;

    public bool HasWeatherName =>
        !string.IsNullOrWhiteSpace(WeatherProvince)
        || !string.IsNullOrWhiteSpace(WeatherCity)
        || !string.IsNullOrWhiteSpace(WeatherDistrict);

    public string WeatherPlaceLabel
    {
        get
        {
            var parts = new[] { WeatherProvince, WeatherCity, WeatherDistrict }
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return parts.Length == 0 ? "" : string.Join(" ", parts);
        }
    }

    /// <summary>Kept so older settings files still load.</summary>
    public string WeatherManualCity { get; set; } = "";

    /// <summary>Show 此电脑 on the icon pages.</summary>
    public bool ShowThisPc { get; set; }

    /// <summary>Show 回收站 on the icon pages.</summary>
    public bool ShowRecycleBin { get; set; }

    /// <summary>Search engines for the search widget (built-ins merged on load if empty).</summary>
    public List<SearchEngineInfo> SearchEngines { get; set; } = SearchEngineInfo.CreateBuiltIns();
    public string DefaultSearchEngineId { get; set; } = "bing";
}

public sealed class SearchEngineInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Use {word} as the query placeholder. {q} is still accepted.</summary>
    public string UrlTemplate { get; set; } = "";
    public bool Enabled { get; set; } = true;

    public string BuildSearchUrl(string query)
    {
        var enc = Uri.EscapeDataString(query);
        return UrlTemplate
            .Replace("{word}", enc, StringComparison.OrdinalIgnoreCase)
            .Replace("{q}", enc, StringComparison.OrdinalIgnoreCase);
    }

    public static List<SearchEngineInfo> CreateBuiltIns() =>
    [
        new() { Id = "bing", Name = "必应", UrlTemplate = "https://www.bing.com/search?q={q}", Enabled = true },
        new() { Id = "baidu", Name = "百度", UrlTemplate = "https://www.baidu.com/s?wd={q}", Enabled = true },
        new() { Id = "google", Name = "Google", UrlTemplate = "https://www.google.com/search?q={q}", Enabled = true },
        new() { Id = "sogou", Name = "搜狗", UrlTemplate = "https://www.sogou.com/web?query={q}", Enabled = true }
    ];
}

public sealed class LibraryData
{
    public List<CategoryInfo> Categories { get; set; } = [];
    public List<ItemInfo> Items { get; set; } = [];
}

public sealed class CategoryInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "默认";
    public int SortOrder { get; set; }
    /// <summary>When false, page is hidden from the notch icon pager.</summary>
    public bool Visible { get; set; } = true;
}

public sealed class ItemInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CategoryId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string ItemKind { get; set; } = "Lnk";
    public string StorageMode { get; set; } = "MovedFile";
    public string? StoredPath { get; set; }
    public string? TargetPath { get; set; }
    public string? OriginalDirectory { get; set; }
    public string? OriginalFileName { get; set; }
    public string Source { get; set; } = "UserDesktop";
    public bool IsBroken { get; set; }
    public string? BrokenReason { get; set; }
    public string? IconCacheKey { get; set; }
    public int SortOrder { get; set; }
    public DateTimeOffset? LastLaunchedAt { get; set; }
}
