# HSHONAD Launcher — Select Game / Proxy / Low Power

This branch adds Windows-side game selection, read-only checks, a local HTTPS CONNECT proxy with an explicit destination allowlist, and game-session monitoring. It deliberately does not import any project-specific identity, endpoints, host mappings, or certificate behavior from the example repository.

## Changes

- **Select Game:** use a Windows file picker to select a game `.exe`; persist the full path under `%LOCALAPPDATA%\HSHONAD\Launcher\game-path.txt`.
- **Fix (read-only):** verify the selected executable exists and show basic local file/folder information. It does not write, replace, or delete game files because the current manifest endpoint has not been shown to provide per-file trusted SHA-256 values and download URLs.
- **HSHONAD Proxy:** listen only on `127.0.0.1:8888`; accept HTTPS CONNECT only for explicitly configured host names. The allowlist is saved to `%LOCALAPPDATA%\HSHONAD\Launcher\proxy-allowlist.txt`. Use exact names (for example, `api.your-domain.example`) or a wildcard subdomain (for example, `*.your-domain.example`). These are examples, not real HSHONAD game domains.
- **Game session:** monitor the selected process roughly every 2.5 seconds. When it is detected, lower the Launcher process priority to `BelowNormal` and start the proxy only when an allowlist exists. When the selected game is no longer detected, stop the proxy and restore the previous Launcher priority.
- **Settings:** the existing HSHONAD Backend API remains the default endpoint; the API URL can still be changed in the UI.

## Important Proxy limitation

This implementation is a regular forward proxy, not a transparent MITM proxy. It does not edit the Windows `hosts` file, install a Root CA, decrypt TLS, or automatically redirect game connections. A game/client must be configured to use `127.0.0.1:8888` for its HTTPS connections to pass through this listener. If the game requires a Host-to-local routing scheme like the reference project uses, HSHONAD's own authorized target domains and routing requirements must be defined first; those values must not be guessed or copied from the example.

## Build and manual checks

Open `HSHONAD Launcher.csproj` in Visual Studio on Windows with the .NET 10 Windows Desktop workload and build the project.

1. Open Launcher and verify Login no longer blocks the initial screen.
2. Select a real game `.exe`; close and reopen Launcher and verify the selected path is restored.
3. Use Fix to run the read-only scan; verify no game files are modified.
4. With no allowlist configured, verify the proxy refuses to start / refuses all destinations.
5. While the selected game is running, save an allowlist of domains you control; verify the proxy binds only to loopback and only allows listed domains on port 443.
6. Close the game; verify the proxy stops and Launcher priority returns to its previous value.
7. Check login and manifest checking still work with the HSHONAD Backend API.

Build and runtime testing must be done in Visual Studio on Windows; the GitHub branch itself is not proof that the project has been built or run successfully.
