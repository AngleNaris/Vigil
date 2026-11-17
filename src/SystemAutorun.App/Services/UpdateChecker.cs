using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace SystemAutorun.App.Services;

internal enum UpdateCheckState
{
    UpToDate,
    UpdateAvailable,
    NoPublishedRelease,
    Failed,
}

internal sealed record UpdateCheckResult(
    UpdateCheckState State,
    string CurrentVersion,
    string? LatestVersion,
    string TargetUrl,
    string Message);

internal static class UpdateChecker
{
    internal const string ProductName = "Vigil";
    internal const string AuthorName = "繁星之子卡萨帝亚";
    internal const string RepositoryUrl = "https://github.com/AngleNaris/Vigil";
    internal const string LatestReleaseApiUrl = "https://api.github.com/repos/AngleNaris/Vigil/releases/latest";

    private static readonly HttpClient SharedClient = CreateHttpClient();

    internal static string CurrentVersionString => ResolveCurrentVersionString();

    internal static Func<CancellationToken, Task<UpdateCheckResult>>? TestCheckOverride { get; set; }

    internal static async Task<UpdateCheckResult> CheckLatestAsync(
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task<UpdateCheckResult>>? customChecker = null)
    {
        var checker = customChecker ?? TestCheckOverride;
        if (checker is not null)
        {
            return await checker(cancellationToken);
        }

        var current = CurrentVersionString;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUrl);
            request.Headers.UserAgent.ParseAdd($"{ProductName}/{current}");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var response = await SharedClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new(
                    UpdateCheckState.NoPublishedRelease,
                    current,
                    null,
                    RepositoryUrl,
                    "仓库尚未发布公开 Release，可前往项目主页查看源码。");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new(
                    UpdateCheckState.Failed,
                    current,
                    null,
                    RepositoryUrl,
                    $"更新服务器返回 HTTP {(int)response.StatusCode}，暂无法确认最新版本。");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeoutCts.Token);
            var root = document.RootElement;
            var tagName = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString()?.Trim() : null;
            var htmlUrl = root.TryGetProperty("html_url", out var urlElement) ? urlElement.GetString()?.Trim() : null;
            var targetUrl = !string.IsNullOrWhiteSpace(htmlUrl) && Uri.TryCreate(htmlUrl, UriKind.Absolute, out var parsedUri)
                && (parsedUri.Scheme == Uri.UriSchemeHttps || parsedUri.Scheme == Uri.UriSchemeHttp)
                ? parsedUri.ToString()
                : RepositoryUrl;

            if (string.IsNullOrWhiteSpace(tagName))
            {
                return new(
                    UpdateCheckState.Failed,
                    current,
                    null,
                    targetUrl,
                    "已连接 GitHub Releases，但响应缺少版本号。");
            }

            var normalizedLatest = NormalizeVersionText(tagName);
            if (IsNewerVersion(normalizedLatest, current))
            {
                return new(
                    UpdateCheckState.UpdateAvailable,
                    current,
                    normalizedLatest,
                    targetUrl,
                    $"发现新版本 v{normalizedLatest}（当前 v{current}），可前往发布页下载更新。");
            }

            return new(
                UpdateCheckState.UpToDate,
                current,
                normalizedLatest,
                targetUrl,
                $"当前已是最新版本 v{current}。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new(
                UpdateCheckState.Failed,
                current,
                null,
                RepositoryUrl,
                "检查更新超时（8 秒），请确认网络连接后重试。");
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            return new(
                UpdateCheckState.Failed,
                current,
                null,
                RepositoryUrl,
                $"无法连接 GitHub Releases：{exception.Message}");
        }
    }

    internal static bool IsNewerVersion(string latest, string current)
    {
        if (TryParseSemanticVersion(latest, out var latestVersion)
            && TryParseSemanticVersion(current, out var currentVersion))
        {
            return latestVersion > currentVersion;
        }
        return !string.Equals(NormalizeVersionText(latest), NormalizeVersionText(current), StringComparison.OrdinalIgnoreCase);
    }

    internal static string NormalizeVersionText(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
        {
            trimmed = trimmed[1..];
        }
        var plusIndex = trimmed.IndexOf('+');
        if (plusIndex >= 0) { trimmed = trimmed[..plusIndex]; }
        return string.IsNullOrWhiteSpace(trimmed) ? "1.0.0" : trimmed;
    }

    private static bool TryParseSemanticVersion(string raw, out Version version)
    {
        var text = NormalizeVersionText(raw);
        var dashIndex = text.IndexOf('-');
        if (dashIndex >= 0) { text = text[..dashIndex]; }
        if (Version.TryParse(text, out var parsed))
        {
            version = new Version(
                Math.Max(0, parsed.Major),
                Math.Max(0, parsed.Minor),
                Math.Max(0, parsed.Build),
                Math.Max(0, parsed.Revision));
            return true;
        }
        version = new Version(0, 0, 0, 0);
        return false;
    }

    private static string ResolveCurrentVersionString()
    {
        var assembly = typeof(UpdateChecker).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            return NormalizeVersionText(informational);
        }
        var version = assembly.GetName().Version;
        return version is null ? "1.0.0" : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        return client;
    }
}
