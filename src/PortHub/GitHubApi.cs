using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PortHub;

internal sealed record GitHubRepository(string Owner, string Name, string FullName, string Description, Uri HtmlUrl);
public sealed record ReleaseAsset(long Id, string Name, long Size, Uri DownloadUrl);
internal sealed record GitHubRelease(long Id, string TagName, Uri HtmlUrl, IReadOnlyList<ReleaseAsset> Assets);
internal sealed record RepositoryOverview(GitHubRepository Repository, GitHubRelease? LatestRelease);
internal sealed record PortHubPublicConfiguration(string? ApiBaseUrl);

internal sealed class GitHubApi
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _api;
    private readonly string? _proxyBaseUrl;
    public HttpClient Downloads { get; }

    public GitHubApi()
    {
        _api = NewClient();
        _api.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _api.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        Downloads = NewClient();
        // Release downloads are from github.com; never attach a PAT to this client.
        Downloads.Timeout = Timeout.InfiniteTimeSpan;
        _proxyBaseUrl = ReadPublicProxyUrl();
    }

    private static HttpClient NewClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PortHub/0.1.8");
        return client;
    }

    private static string? ReadPublicProxyUrl()
    {
        // Only a PUBLIC server URL lives in this file; there is never a secret in the desktop app.
        string file = Path.Combine(AppContext.BaseDirectory, "porthub.config.json");
        if (!File.Exists(file)) return null;
        try
        {
            var settings = JsonSerializer.Deserialize<PortHubPublicConfiguration>(File.ReadAllText(file), JsonOptions);
            string? configured = settings?.ApiBaseUrl?.Trim();
            if (string.IsNullOrEmpty(configured)) return null;
            if (!Uri.TryCreate(configured, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)
                || uri.Query.Length > 0 || uri.Fragment.Length > 0 || !uri.IsDefaultPort)
                throw new InvalidDataException("ApiBaseUrl должен быть обычным HTTPS-адресом публичного сервера.");
            return uri.AbsoluteUri.TrimEnd('/');
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Некорректный porthub.config.json: " + ex.Message, ex);
        }
    }

    public Task<RepositoryOverview> GetOverviewAsync(string owner, string name, CancellationToken token = default)
    {
        if (_proxyBaseUrl is not null)
            return GetProxyOverviewAsync(owner, name, token);
        return GetPublicOverviewAsync(owner, name, token);
    }

    private async Task<RepositoryOverview> GetProxyOverviewAsync(string owner, string name, CancellationToken token)
    {
        string url = $"{_proxyBaseUrl}/api/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/overview";
        using var response = await _api.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException("Репозиторий не найден или не является публичным.");
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new InvalidOperationException("Сервер PortHub ограничил запросы. Повтори синхронизацию позднее.");
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Сервер PortHub вернул HTTP {(int)response.StatusCode}.");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        var result = await JsonSerializer.DeserializeAsync<RepositoryOverview>(stream, JsonOptions, token);
        if (result?.Repository is null)
            throw new JsonException("Сервер вернул неполную информацию о репозитории.");
        return result;
    }

    private async Task<RepositoryOverview> GetPublicOverviewAsync(string owner, string name, CancellationToken token)
    {
        // Without a deployed proxy, public GitHub access continues to work without a token.
        var root = $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}";
        using var info = await FetchJsonAsync(root, false, token)
            ?? throw new InvalidOperationException("Репозиторий не найден.");
        var repo = info.RootElement;
        if (repo.TryGetProperty("private", out var privateProperty) && privateProperty.GetBoolean())
            throw new InvalidOperationException("PortHub поддерживает только публичные репозитории.");
        string realOwner = repo.GetProperty("owner").GetProperty("login").GetString() ?? owner;
        string realName = repo.GetProperty("name").GetString() ?? name;
        string description = repo.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
            ? d.GetString() ?? "" : "";
        var repository = new GitHubRepository(realOwner, realName,
            repo.GetProperty("full_name").GetString() ?? $"{realOwner}/{realName}",
            description,
            new Uri(repo.GetProperty("html_url").GetString() ?? $"https://github.com/{realOwner}/{realName}"));

        string releasesUrl = $"https://api.github.com/repos/{Uri.EscapeDataString(realOwner)}/{Uri.EscapeDataString(realName)}/releases/latest";
        using var releaseData = await FetchJsonAsync(releasesUrl, true, token);
        if (releaseData is null)
            return new RepositoryOverview(repository, null);

        var releaseJson = releaseData.RootElement;
        var assets = new List<ReleaseAsset>();
        if (releaseJson.TryGetProperty("assets", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                var urlText = item.GetProperty("browser_download_url").GetString();
                if (Uri.TryCreate(urlText, UriKind.Absolute, out var uri)
                    && uri.Scheme == Uri.UriSchemeHttps
                    && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
                {
                    assets.Add(new ReleaseAsset(
                        item.GetProperty("id").GetInt64(),
                        item.GetProperty("name").GetString() ?? "asset",
                        item.GetProperty("size").GetInt64(), uri));
                }
            }
        }

        var release = new GitHubRelease(
            releaseJson.GetProperty("id").GetInt64(),
            releaseJson.GetProperty("tag_name").GetString() ?? "",
            new Uri(releaseJson.GetProperty("html_url").GetString() ?? repository.HtmlUrl.ToString()),
            assets);
        return new RepositoryOverview(repository, release);
    }

    private async Task<JsonDocument?> FetchJsonAsync(string url, bool notFoundIsEmpty, CancellationToken token)
    {
        using var response = await _api.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            if (notFoundIsEmpty) return null;
            throw new InvalidOperationException("Репозиторий не найден или закрыт для публичного доступа.");
        }
        if (response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new InvalidOperationException("GitHub ограничил запросы. Повтори синхронизацию позднее.");
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub API вернул HTTP {(int)response.StatusCode}.");

        await using var stream = await response.Content.ReadAsStreamAsync(token);
        return await JsonDocument.ParseAsync(stream, cancellationToken: token);
    }
}
