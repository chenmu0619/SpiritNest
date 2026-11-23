using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace SpiritNest.Services;

/// <summary>Fetch page title + favicon for URL navigation items.</summary>
public static class UrlFetchService
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        c.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) SpiritNest/0.1");
        return c;
    }

    public static string NormalizeUrl(string raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0) return "";
        if (!s.Contains("://", StringComparison.Ordinal))
            s = "https://" + s;
        return Uri.TryCreate(s, UriKind.Absolute, out var u) &&
               (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
            ? u.ToString()
            : "";
    }

    public static async Task<UrlFetchResult> FetchAsync(string rawUrl, CancellationToken ct = default)
    {
        var url = NormalizeUrl(rawUrl);
        if (url.Length == 0)
            return UrlFetchResult.Fail("网址无效");

        string? title = null;
        byte[]? icon = null;
        try
        {
            using var pageResp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            var media = pageResp.Content.Headers.ContentType?.MediaType ?? "";
            string? html = null;
            if (pageResp.IsSuccessStatusCode && media.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = await pageResp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                html = DecodeHtml(bytes, pageResp.Content.Headers.ContentType?.CharSet);
                title = ExtractTitle(html);
            }

            var baseUri = new Uri(url);
            icon = await TryDownloadFaviconAsync(baseUri, html, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new UrlFetchResult(false, url, title, icon, ex.Message);
        }

        return new UrlFetchResult(true, url, title, icon, null);
    }

    private static string DecodeHtml(byte[] bytes, string? charset)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(charset))
                return Encoding.GetEncoding(charset).GetString(bytes);
        }
        catch { /* fall through */ }
        return Encoding.UTF8.GetString(bytes);
    }

    private static string? ExtractTitle(string? html)
    {
        if (string.IsNullOrEmpty(html)) return null;
        var m = Regex.Match(html, @"<title[^>]*>(.*?)</title>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!m.Success) return null;
        var t = Regex.Replace(m.Groups[1].Value, @"\s+", " ").Trim();
        t = System.Net.WebUtility.HtmlDecode(t);
        return t.Length == 0 ? null : t;
    }

    private static async Task<byte[]?> TryDownloadFaviconAsync(Uri page, string? html, CancellationToken ct)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(html))
        {
            foreach (Match m in Regex.Matches(html,
                         @"<link[^>]+rel\s*=\s*[""'][^""']*icon[^""']*[""'][^>]*>",
                         RegexOptions.IgnoreCase))
            {
                var hm = Regex.Match(m.Value, @"href\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
                if (hm.Success) candidates.Add(hm.Groups[1].Value);
            }
        }

        candidates.Add("/favicon.ico");
        candidates.Add("/favicon.png");

        foreach (var href in candidates)
        {
            try
            {
                if (!Uri.TryCreate(page, href, out var iconUri)) continue;
                using var resp = await Http.GetAsync(iconUri, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) continue;
                var data = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                if (data.Length is < 32 or > 2_000_000) continue;
                var png = IconService.TryNormalizeToPng(data);
                if (png is { Length: > 0 }) return png;
            }
            catch { /* try next */ }
        }

        return null;
    }
}

public readonly record struct UrlFetchResult(
    bool Success,
    string Url,
    string? Title,
    byte[]? IconPng,
    string? Error)
{
    public static UrlFetchResult Fail(string error) => new(false, "", null, null, error);
}
