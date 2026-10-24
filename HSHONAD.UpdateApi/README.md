# HSHONAD Update API

Small .NET 10 API that reads the latest public GitHub Release and exposes a launcher update manifest.

## Endpoints

- `GET /health` — Render health check.
- `GET /api/updates/launcher?currentVersion=1.0.0` — latest version, release asset URL, SHA-256 digest (when GitHub provides it), and update availability.

## Deploy on Render

Create a Web Service from this repository using Docker.
- Root Directory: repository root.
- Dockerfile Path: `HSHONAD.UpdateApi/Dockerfile`.
- Health Check Path: `/health`.
- Optional environment variables: `GitHub__Owner=HSHNAD234` and `GitHub__Repository=Launcher-code` (defaults already match).

## Release workflow

Push a tag such as `v1.0.1` to trigger `.github/workflows/release-launcher.yml`. The workflow publishes the Windows x64 app and attaches `HSHONAD-Launcher-win-x64.zip` to the GitHub Release. The launcher updater only downloads that named asset from this repository's releases.
