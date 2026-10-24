# HSHONAD Launcher — Select Game / Proxy / Low Power

This branch adds Windows-side game selection, read-only checks, a local HTTPS CONNECT proxy with an explicit destination allowlist, and game-session monitoring. It deliberately does not import any project-specific identity, endpoints, host mappings, or certificate behavior from the example repository.

## Changes

- **Select Game:** use a Windows file picker to select a game `.exe`; persist the full path under `%LOCALAPPDATA%\HSHONAD\Launcher\game-path.txt`.
- **Launch gate:** the Launch Game UI and native C# handler both require an approved Discord/whitelist result for the current Launcher session.
- **Fix (game files):** verify the selected executable exists and show basic local file/folder information. This part is read-only; it does not write, replace, or delete game files because the current manifest endpoint has not been shown to provide per-file trusted SHA-256 values and download URLs.
- **Windows Hosts repair:** inspect and explicitly remove only custom entries for `api.homesweethomegame.com`, `hshsapi.homesweethomegame.com`, `logapi.homesweethomegame.com`, and `boatltpk`. Other aliases and unrelated lines are preserved, and a timestamped backup is made before changes. Hosts repair may require running the Launcher as Administrator.
- **HSHONAD Proxy:** listen only on `127.0.0.1:8888`; accept HTTPS CONNECT only for explicitly configured host names. The allowlist is saved to `%LOCALAPPDATA%\HSHONAD\Launcher\proxy-allowlist.txt`. Use exact names (for example, `api.your-domain.example`) or a wildcard subdomain (for example, `*.your-domain.example`). These are examples, not real HSHONAD game domains.
- **Game session:** monitor the selected process roughly every 2.5 seconds. When it is detected, lower the Launcher process priority to `BelowNormal` and start the proxy only when an allowlist exists. When the selected game is no longer detected, stop the proxy and restore the previous Launcher priority.
- **Settings:** the existing HSHONAD Backend API remains the default endpoint; the API URL can still be changed in the UI.

## Important Proxy limitation

This implementation is a regular forward proxy, not a transparent MITM proxy. It does not install a Root CA or decrypt TLS. The optional Hosts repair action only removes explicit overrides for the four listed HSH API domains so Windows can resolve them through normal DNS; it does not redirect them to localhost or another IP. A game/client must be configured to use `127.0.0.1:8888` for its HTTPS connections to pass through this listener. If the game requires a Host-to-local routing scheme like the reference project uses, HSHONAD's own authorized target domains and routing requirements must be defined first; those values must not be guessed or copied from the example.

## Build and manual checks

Open `HSHONAD Launcher.csproj` in Visual Studio on Windows with the .NET 10 Windows Desktop workload and build the project.

1. Open Launcher and verify Login no longer blocks the initial screen.
2. Select a real game `.exe`; close and reopen Launcher and verify the selected path is restored.
3. Use Fix to run the read-only scan; verify no game files are modified.
4. Inspect Hosts; verify only entries matching the four managed names are shown. Run the restore action only when you intend to remove those overrides; confirm a timestamped backup is created and unrelated entries remain intact.
5. With no allowlist configured, verify the proxy refuses to start / refuses all destinations.
6. While the selected game is running, save an allowlist of domains you control; verify the proxy binds only to loopback and only allows listed domains on port 443.
7. Close the game; verify the proxy stops and Launcher priority returns to its previous value.
8. Check login and manifest checking still work with the HSHONAD Backend API.

Build and runtime testing must be done in Visual Studio on Windows; the GitHub branch itself is not proof that the project has been built or run successfully.
