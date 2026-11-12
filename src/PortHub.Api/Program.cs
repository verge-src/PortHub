using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;

var builder = WebApplication.CreateBuilder(args);

// PAT MUST stay in the hosting provider's secret environment variables.
// It is never sent to the desktop application or embedded in compiled client files.
string token = (Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? "").Trim();
if (string.IsNullOrWhiteSpace(token))
    throw new InvalidOperationException("GITHUB_TOKEN is missing. Set a NEW token in the server environment, not in source code.");

builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<GitHubGateway>(client =>
{
    client.BaseAddress = new Uri("https://api.github.com/");
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("PortHub-API/1.0.1");
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Global cap controls how fast a public endpoint can spend the shared GitHub budget.
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
        RateLimitPartition.GetFixedWindowLimiter("server", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 30,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
    // Per-IP limit reduces abuse. A production deployment should also use an edge/WAF limit.
    options.AddPolicy("public-repos", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

var app = builder.Build();
app.UseRouting();
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    await next();
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/api/repos/{owner}/{repo}/overview", GetOverviewHandler)
    .RequireRateLimiting("public-repos");

// Global repository discovery via GitHub Search. Only public repositories are returned.
// Paginated and cached; release details are fetched only after selecting a project.
app.MapGet("/api/search/repositories", SearchRepositoriesHandler)
    .RequireRateLimiting("public-repos");

app.Run();

static async Task<IResult> GetOverviewHandler(
    string owner, string repo, GitHubGateway github, IMemoryCache cache, CancellationToken cancellationToken)
{
    if (!ValidOwner(owner) || !ValidRepo(repo))
        return Results.BadRequest(new { message = "Некорректное имя публичного репозитория." });

    string cacheKey = $"public-repository:{owner.ToLowerInvariant()}/{repo.ToLowerInvariant()}";
    if (cache.TryGetValue(cacheKey, out RepoOverview? cached) && cached is not null)
        return Results.Ok(cached);

    try
    {
        RepoOverview? result = await github.GetPublicOverviewAsync(owner, repo, cancellationToken);
        if (result is null)
            return Results.NotFound(new { message = "Публичный репозиторий не найден." });
        cache.Set(cacheKey, result, TimeSpan.FromMinutes(2));
        return Results.Ok(result);
    }
    catch (GitHubUpstreamException ex)
    {
        return Results.Json(new { message = ex.PublicMessage }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (HttpRequestException)
    {
        return Results.Json(new { message = "Не удалось подключиться к GitHub." }, statusCode: StatusCodes.Status502BadGateway);
    }
    catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        return Results.Json(new { message = "GitHub не ответил вовремя." }, statusCode: StatusCodes.Status504GatewayTimeout);
    }
    catch (JsonException)
    {
        return Results.Json(new { message = "GitHub вернул некорректные данные." }, statusCode: StatusCodes.Status502BadGateway);
    }
}

static async Task<IResult> SearchRepositoriesHandler(
    string? q, int? page, GitHubGateway github, IMemoryCache cache, CancellationToken cancellationToken)
{
    string search = (q ?? "").Trim();
    int number = page ?? 1;
    if (number is < 1 or > 34 || search.Length > 80 || search.Any(char.IsControl))
        return Results.BadRequest(new { message = "Неверный поисковый запрос или страница." });

    string cacheKey = $"github-search:{number}:{search.ToLowerInvariant()}";
    if (cache.TryGetValue(cacheKey, out GlobalSearchResponse? cached) && cached is not null)
        return Results.Ok(cached);

    try
    {
        GlobalSearchResponse result = await github.SearchPublicRepositoriesAsync(search, number, cancellationToken);
        cache.Set(cacheKey, result, TimeSpan.FromMinutes(5));
        return Results.Ok(result);
    }
    catch (GitHubUpstreamException ex)
    {
        return Results.Json(new { message = ex.PublicMessage }, statusCode: 503);
    }
    catch (HttpRequestException)
    {
        return Results.Json(new { message = "Не удалось подключиться к GitHub." }, statusCode: 502);
    }
    catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        return Results.Json(new { message = "GitHub не ответил вовремя." }, statusCode: 504);
    }
    catch (JsonException)
    {
        return Results.Json(new { message = "GitHub вернул некорректные данные." }, statusCode: 502);
    }
}

static bool ValidOwner(string value) =>
    Regex.IsMatch(value, @"^[a-zA-Z0-9][a-zA-Z0-9-]{0,38}$", RegexOptions.CultureInvariant)
    && !value.EndsWith('-');
static bool ValidRepo(string value) =>
    Regex.IsMatch(value, @"^[a-zA-Z0-9_.-]{1,100}$", RegexOptions.CultureInvariant)
    && value is not "." and not "..";

internal sealed record RepoDto(string Owner, string Name, string FullName, string Description, string HtmlUrl);
internal sealed record AssetDto(long Id, string Name, long Size, string DownloadUrl);
internal sealed record ReleaseDto(long Id, string TagName, string HtmlUrl, IReadOnlyList<AssetDto> Assets);
internal sealed record RepoOverview(RepoDto Repository, ReleaseDto? LatestRelease);
internal sealed record GlobalSearchResponse(IReadOnlyList<RepoOverview> Items,
    int TotalCount, bool HasMore, bool IncompleteResults, int Page);

internal sealed class GitHubUpstreamException(string message) : Exception(message)
{
    public string PublicMessage => Message;
}

internal sealed class GitHubGateway(HttpClient client)
{
    public async Task<RepoOverview?> GetPublicOverviewAsync(string owner, string repo, CancellationToken ct)
    {
        string safeOwner = Uri.EscapeDataString(owner);
        string safeRepo = Uri.EscapeDataString(repo);
        using JsonDocument? repositoryData = await GetJsonAsync($"repos/{safeOwner}/{safeRepo}", ct);
        if (repositoryData is null) return null;

        JsonElement repository = repositoryData.RootElement;
        // Important: this PAT may have private access. Never return private repo metadata,
        // and never query releases until the public flag has been checked.
        if (!repository.TryGetProperty("private", out var privateFlag)
            || privateFlag.ValueKind != JsonValueKind.False)
            return null;

        string canonicalOwner = repository.GetProperty("owner").GetProperty("login").GetString() ?? owner;
        string canonicalName = repository.GetProperty("name").GetString() ?? repo;
        string fullName = repository.GetProperty("full_name").GetString() ?? $"{canonicalOwner}/{canonicalName}";
        string description = repository.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
            ? d.GetString() ?? "" : "";
        string repoHtml = $"https://github.com/{Uri.EscapeDataString(canonicalOwner)}/{Uri.EscapeDataString(canonicalName)}";
        var dto = new RepoDto(canonicalOwner, canonicalName, fullName, description, repoHtml);

        ReleaseDto? release = await GetLatestReleaseAsync(canonicalOwner, canonicalName, repoHtml, ct);
        return new RepoOverview(dto, release);
    }

    public async Task<GlobalSearchResponse> SearchPublicRepositoriesAsync(
        string search, int page, CancellationToken ct)
    {
        // The default is a discovery page; custom queries search all public GitHub repos.
        // GitHub Search returns at most 1000 results for a single query.
        string query = string.IsNullOrWhiteSpace(search)
            ? "stars:>5000 archived:false fork:false is:public"
            : $"{search} in:name,description archived:false fork:false is:public";
        string relativeUrl = $"search/repositories?q={Uri.EscapeDataString(query)}&per_page=30&page={page}"
            + (string.IsNullOrWhiteSpace(search) ? "&sort=stars&order=desc" : "");
        using JsonDocument? payload = await GetJsonAsync(relativeUrl, ct);
        if (payload is null || payload.RootElement.ValueKind != JsonValueKind.Object)
            throw new GitHubUpstreamException("GitHub не вернул результаты поиска.");

        JsonElement root = payload.RootElement;
        var matches = new List<RepoOverview>();
        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in items.EnumerateArray())
            {
                // PAT may be authorized for private repos: never return those.
                if (!item.TryGetProperty("private", out var privacy) || privacy.ValueKind != JsonValueKind.False)
                    continue;
                if (item.TryGetProperty("archived", out var archived) && archived.ValueKind == JsonValueKind.True)
                    continue;
                if (item.TryGetProperty("fork", out var fork) && fork.ValueKind == JsonValueKind.True)
                    continue;
                string owner = item.GetProperty("owner").GetProperty("login").GetString() ?? "";
                string name = item.GetProperty("name").GetString() ?? "";
                if (!Regex.IsMatch(owner, @"^[a-zA-Z0-9][a-zA-Z0-9-]{0,38}$", RegexOptions.CultureInvariant)
                    || owner.EndsWith('-')
                    || !Regex.IsMatch(name, @"^[a-zA-Z0-9_.-]{1,100}$", RegexOptions.CultureInvariant)
                    || name is "." or "..") continue;
                string description = item.TryGetProperty("description", out var d)
                    && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";
                string html = $"https://github.com/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}";
                matches.Add(new RepoOverview(new RepoDto(owner, name, $"{owner}/{name}", description, html), null));
            }
        }
        int total = root.TryGetProperty("total_count", out var count) ? count.GetInt32() : 0;
        bool incomplete = root.TryGetProperty("incomplete_results", out var flag)
            && flag.ValueKind == JsonValueKind.True;
        bool hasMore = matches.Count > 0 && page < 34 && page * 30 < Math.Min(1000, total);
        return new GlobalSearchResponse(matches, total, hasMore, incomplete, page);
    }

    private async Task<ReleaseDto?> GetLatestReleaseAsync(string owner, string repo, string repoHtml, CancellationToken ct)
    {
        // Only after checking a repo is public or obtaining it from the public listing.
        using JsonDocument? releaseData = await GetJsonAsync(
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/releases/latest", ct);
        if (releaseData is null) return null;

        JsonElement release = releaseData.RootElement;
        var assets = new List<AssetDto>();
        if (release.TryGetProperty("assets", out var rawAssets) && rawAssets.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement asset in rawAssets.EnumerateArray())
            {
                string name = asset.GetProperty("name").GetString() ?? "";
                string? url = asset.GetProperty("browser_download_url").GetString();
                if (name.Length is < 1 or > 180 || name is "." or ".."
                    || name.IndexOfAny(['/', '\\']) >= 0 || name.Any(char.IsControl)
                    || !Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)
                    || parsed.Scheme != Uri.UriSchemeHttps
                    || !parsed.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
                    continue;
                assets.Add(new AssetDto(asset.GetProperty("id").GetInt64(), name,
                    asset.GetProperty("size").GetInt64(), parsed.AbsoluteUri));
            }
        }
        string releaseUrl = release.GetProperty("html_url").GetString() ?? repoHtml;
        if (!Uri.TryCreate(releaseUrl, UriKind.Absolute, out Uri? validatedUrl)
            || validatedUrl.Scheme != Uri.UriSchemeHttps
            || !validatedUrl.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            releaseUrl = repoHtml;

        return new ReleaseDto(release.GetProperty("id").GetInt64(),
            release.GetProperty("tag_name").GetString() ?? "", releaseUrl, assets);
    }

    private async Task<JsonDocument?> GetJsonAsync(string relativeUrl, CancellationToken ct)
    {
        using var response = await client.GetAsync(relativeUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new GitHubUpstreamException("GitHub ограничил запросы. Попробуй позднее.");
        if (!response.IsSuccessStatusCode)
            throw new GitHubUpstreamException($"GitHub временно недоступен (HTTP {(int)response.StatusCode}).");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }
}
