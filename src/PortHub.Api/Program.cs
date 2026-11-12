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
    client.DefaultRequestHeaders.UserAgent.ParseAdd("PortHub-API/0.1.8");
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

        // GitHub /latest excludes prereleases and drafts, and is safe for public repos.
        using JsonDocument? releaseData = await GetJsonAsync(
            $"repos/{Uri.EscapeDataString(canonicalOwner)}/{Uri.EscapeDataString(canonicalName)}/releases/latest", ct);
        if (releaseData is null) return new RepoOverview(dto, null);

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

        var releaseDto = new ReleaseDto(release.GetProperty("id").GetInt64(),
            release.GetProperty("tag_name").GetString() ?? "", releaseUrl, assets);
        return new RepoOverview(dto, releaseDto);
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
