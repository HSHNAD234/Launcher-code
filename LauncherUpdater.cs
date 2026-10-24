using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HSHONAD_Launcher
{
    internal static class LauncherUpdater
    {
        private const string LatestReleaseUrl = "https://api.github.com/repos/HSHNAD234/Launcher-code/releases/latest";
        private const string PackageName = "HSHONAD-Launcher-win-x64.zip";
        private static readonly string UpdateDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HSHONAD Launcher", "Updates");
        private static readonly string PendingPackagePath = Path.Combine(UpdateDirectory, "pending.zip");
        private static readonly HttpClient Http = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("HSHONAD-Launcher-Updater");
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            return client;
        }

        public static async Task<bool> CheckAndApplyAsync()
        {
            try
            {
                // Install a package downloaded during the previous session.
                if (File.Exists(PendingPackagePath))
                {
                    StartPendingUpdateInstaller();
                    Application.Exit();
                    return true;
                }

                using var response = await Http.GetAsync(LatestReleaseUrl);
                if (!response.IsSuccessStatusCode) return false;

                await using var responseStream = await response.Content.ReadAsStreamAsync();
                using var release = await JsonDocument.ParseAsync(responseStream);
                var root = release.RootElement;

                if (!TryParseVersion(ReadString(root, "tag_name"), out var latestVersion)) return false;
                Version currentVersion = typeof(LauncherUpdater).Assembly.GetName().Version ?? new Version(1, 0, 0);
                if (latestVersion <= currentVersion) return false;

                if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                    return false;

                string? downloadUrl = null;
                string? expectedHash = null;
                foreach (var asset in assets.EnumerateArray())
                {
                    if (!string.Equals(ReadString(asset, "name"), PackageName, StringComparison.Ordinal)) continue;
                    downloadUrl = ReadString(asset, "browser_download_url");
                    expectedHash = ReadString(asset, "digest");
                    break;
                }

                if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var packageUri) ||
                    packageUri.Scheme != Uri.UriSchemeHttps ||
                    !string.Equals(packageUri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
                    return false;

                Directory.CreateDirectory(UpdateDirectory);
                string tempZipPath = PendingPackagePath + ".download";
                try
                {
                    using (var packageResponse = await Http.GetAsync(packageUri, HttpCompletionOption.ResponseHeadersRead))
                    {
                        packageResponse.EnsureSuccessStatusCode();
                        await using var source = await packageResponse.Content.ReadAsStreamAsync();
                        await using var destination = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None);
                        await source.CopyToAsync(destination);
                    }

                    if (!string.IsNullOrWhiteSpace(expectedHash) &&
                        expectedHash.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    {
                        await using var verifyStream = File.OpenRead(tempZipPath);
                        byte[] hash = await SHA256.HashDataAsync(verifyStream);
                        string actualHash = Convert.ToHexString(hash).ToLowerInvariant();
                        string releaseHash = expectedHash.Substring("sha256:".Length).ToLowerInvariant();
                        if (!CryptographicOperations.FixedTimeEquals(
                            Encoding.ASCII.GetBytes(actualHash),
                            Encoding.ASCII.GetBytes(releaseHash)))
                            throw new InvalidDataException("ไฟล์อัปเดตไม่ผ่านการตรวจสอบ SHA-256");
                    }

                    if (File.Exists(PendingPackagePath)) File.Delete(PendingPackagePath);
                    File.Move(tempZipPath, PendingPackagePath);
                }
                finally
                {
                    if (File.Exists(tempZipPath)) File.Delete(tempZipPath);
                }

                MessageBox.Show(
                    $"ดาวน์โหลด Launcher เวอร์ชัน {latestVersion} สำเร็จแล้ว\n\n" +
                    "อัปเดตจะถูกติดตั้งเมื่อคุณปิด Launcher แล้วเปิดใหม่ครั้งถัดไป",
                    "HSHONAD Launcher Update",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);

                // Keep the current session running. The user chooses when to close it.
                return false;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "ตรวจสอบหรือดาวน์โหลดอัปเดตไม่สำเร็จ จึงเปิดเวอร์ชันปัจจุบันต่อไป\n\n" + ex.Message,
                    "HSHONAD Launcher Update",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return false;
            }
        }

        private static void StartPendingUpdateInstaller()
        {
            string installPath = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string exePath = Environment.ProcessPath ?? throw new InvalidOperationException("ไม่พบตำแหน่งไฟล์ Launcher");
            string exeName = Path.GetFileName(exePath);
            string processName = Path.GetFileNameWithoutExtension(exeName);
            string workPath = Path.Combine(UpdateDirectory, "install-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workPath);

            string scriptPath = Path.Combine(workPath, "apply-update.ps1");
            string extractPath = Path.Combine(workPath, "extract");
            string logPath = Path.Combine(UpdateDirectory, "update.log");

            string script = $@"
$ErrorActionPreference = 'Stop'
$install = '{EscapePowerShell(installPath)}'
$zip = '{EscapePowerShell(PendingPackagePath)}'
$stage = '{EscapePowerShell(extractPath)}'
$processName = '{EscapePowerShell(processName)}'
$exeName = '{EscapePowerShell(exeName)}'
$log = '{EscapePowerShell(logPath)}'
try {{
    while (Get-Process -Name $processName -ErrorAction SilentlyContinue) {{ Start-Sleep -Milliseconds 500 }}
    Expand-Archive -LiteralPath $zip -DestinationPath $stage -Force
    Copy-Item -Path (Join-Path $stage '*') -Destination $install -Recurse -Force
    $exe = Join-Path $install $exeName
    if (-not (Test-Path -LiteralPath $exe)) {{ throw ""Updated executable not found: $exe"" }}
    Remove-Item -LiteralPath $zip -Force
    Start-Process -FilePath $exe
}} catch {{
    $_ | Out-String | Set-Content -LiteralPath $log -Encoding UTF8
}}
";
            File.WriteAllText(scriptPath, script, new UTF8Encoding(false));

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            Process.Start(startInfo);
        }

        private static string ReadString(JsonElement element, string property) =>
            element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty : string.Empty;

        private static bool TryParseVersion(string tag, out Version version)
        {
            string normalized = tag.Trim();
            if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase)) normalized = normalized.Substring(1);
            int suffixIndex = normalized.IndexOfAny(new[] { '-', '+' });
            if (suffixIndex >= 0) normalized = normalized.Substring(0, suffixIndex);
            return Version.TryParse(normalized, out version!);
        }

        private static string EscapePowerShell(string value) => value.Replace("'", "''");
    }
}
