# PortHub API

ASP.NET Core 10 backend. Exposes only public GitHub repository metadata and latest published release. Authenticates to GitHub using **GITHUB_TOKEN** from server environment, never returns the token.

## Local run

In a *private terminal session* set `GITHUB_TOKEN` as a process environment variable, then run:

```powershell
dotnet run --project src/PortHub.Api/PortHub.Api.csproj
```

The server **will not start** when `GITHUB_TOKEN` is missing. Do not write the token into code, `appsettings.json`, files in your repository, screenshots, shell history, or chat messages.

## Docker

From the repository root:

```sh
docker build -f src/PortHub.Api/Dockerfile -t porthub-api .
```

Configure `GITHUB_TOKEN` through your platform's protected environment-variable UI when deploying. Use HTTPS on the public domain. Do not publish the raw HTTP port without HTTPS termination.

## Public endpoints

- `GET /health` basic check
- `GET /api/repos/{owner}/{repo}/overview` returns a public repo summary and latest stable published release, or 404 for private/missing repos

The proxy rejects private repositories even if the PAT has access, filters downloaded asset URLs to github.com, caches successful results for 2 minutes, and rate-limits requests. The server never proxies arbitrary URLs, returns secret headers, or sends tokens to clients. For production, also configure edge rate limiting and request monitoring.

## Automatic catalog


## Global search endpoint (v1.0.1)

`GET /api/search/repositories?q=<search text>&page=1` returns a paginated `GlobalSearchResponse` containing `items`, `totalCount`, `hasMore`, `incompleteResults`, and `page`. Omit `q` to get popular global public repositories. Never put the GitHub PAT in the URL or public code. Search has a 1000-result ceiling per query and is subject to the shared GitHub API search rate limit.
