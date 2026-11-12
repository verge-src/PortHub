# PortHub v1.0.0 | First public Windows release

Real Avalonia Windows application + a separate ASP.NET Core backend for authenticated GitHub metadata. This version is based on the approved v0.1.7.2 design; MainWindow.axaml and MainWindow.axaml.cs are unchanged.

## Security first

The PAT previously shared in a conversation must be treated as **exposed**: revoke it under https://github.com/settings/personal-access-tokens and create a **NEW fine-grained PAT with the minimum permissions**. Do not send the new PAT in chat, put it in an EXE, add it to a config/JSON file, or commit it to GitHub. The server uses the `GITHUB_TOKEN` environment variable, supplied through the host's secret settings.

## What works

- With no hosted server configured: PortHub continues to query public GitHub repos and releases directly without a token. Existing UI, repository management, downloads, and the default `%APPDATA%\porthub\apps` download path remain unchanged.
- With a deployed server URL in the public config: PortHub fetches public repository metadata and latest stable release information from the server, using the server's PAT. Users never receive the PAT.
- Release assets are downloaded from GitHub directly using unauthenticated public URLs, and only HTTPS `github.com` URLs are permitted.
- Backend checks that repositories are **public**, even if the server's PAT can also see private repositories. It filters fields, caches successful results for two minutes, applies rate limits, and never accepts arbitrary external URL requests.

## Connect PortHub to your hosted server

1. Revoke the exposed PAT and create a new least-privilege fine-grained token. For public GitHub repositories, read-only access is enough. There is no need for write/admin permissions.
2. Deploy `src/PortHub.Api` as an ASP.NET Core 10 web service, or deploy it through Docker using `src/PortHub.Api/Dockerfile` from the repository root. Provide HTTPS on its public domain. Do **not** publish secrets in a repository or screenshot.
3. In the host's secure environment variable settings, set `GITHUB_TOKEN` to the new token. Restart the server. The server intentionally fails at startup if the variable is missing.
4. Visit `https://<YOUR-API-DOMAIN>/health` and check that it returns `{"status":"ok"}`. This endpoint does not use the token or expose it.
5. In `src/PortHub/porthub.config.json`, enter the **public HTTPS base URL** of your server as `ApiBaseUrl`, then build or publish the Windows application. Only the server URL goes into this file. The file is copied alongside the Windows executable.
6. Publish and distribute the Windows app. Users get the public server URL, **never** the PAT.

For the public v1.0.0 Windows build, `porthub.config.json` contains the public HTTPS endpoint `https://porthub-api.onrender.com`. The URL is not a secret. The PAT remains only in Render's protected `GITHUB_TOKEN` setting.

## Local development

Desktop app: run `START.cmd` (requires .NET 10 SDK).

Server: with `GITHUB_TOKEN` set securely in the server process environment, run:

```powershell
dotnet run --project src/PortHub.Api/PortHub.Api.csproj
```

Server endpoints: `GET /health` and `GET /api/repos/{owner}/{repo}/overview`. Never pass arbitrary URLs or credentials in client requests.

## Project structure

- `src/PortHub`: Windows Avalonia client; no PAT, design unchanged
- `src/PortHub/porthub.config.json`: public HTTPS server URL only
- `src/PortHub/GitHubApi.cs`: reads public config, uses proxy when configured, otherwise public GitHub API
- `src/PortHub.Api`: server-side GitHub API with secret authentication, public repo filtering and cache/rate limits
- `src/PortHub.Api/Dockerfile`: deployment container

## Build verification

C# was edited with static source checks. This workspace has no .NET SDK, so full .NET compilation and Windows UI testing **could not** be run here. No PAT or other credentials are present in the archive. A running hosted backend cannot be provided until a real HTTPS host and a new private server-side token are configured.
