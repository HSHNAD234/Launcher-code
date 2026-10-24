using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HSHONAD_Launcher
{
    internal static class LauncherUpdater
    {
        private const string LatestReleaseUrl = "https://api.github.com/repos/HSHNAD234/Launcher-code/releases/latest";
        private const string PackageName = "HSHONAD-Launcher-win-x64.zip";
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

                string tempRoot = Path.Combine(Path.GetTempPath(), "HSHONAD-Launcher-Update-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempRoot);
                string zipPath = Path.Combine(tempRoot, PackageName);
                string extractPath = Path.Combine(tempRoot, "extract");
                Directory.CreateDirectory(extractPath);

                using (var packageResponse = await Http.GetAsync(packageUri, HttpCompletionOption.ResponseHeadersRead))
                {
                    packageResponse.EnsureSuccessStatusCode();
                    await using var source = await packageResponse.Content.ReadAsStreamAsync();
                    await using var destination = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await source.CopyToAsync(destination);
                }

                if (!string.IsNullOrWhiteSpace(expectedHash) &&
                    expectedHash.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                {
                    await using var verifyStream = File.OpenRead(zipPath);
                    byte[] hash = await SHA256.HashDataAsync(verifyStream);
                    string actualHash = Convert.ToHexString(hash).ToLowerInvariant();
                    string releaseHash = expectedHash.Substring("sha256:".Length).ToLowerInvariant();
                    if (!CryptographicOperations.FixedTimeEquals(
                        System.Text.Encoding.ASCII.GetBytes(actualHash),
                        System.Text.Encoding.ASCII.GetBytes(releaseHash)))
                        throw new InvalidDataException("ไฟล์อัปเดตไม่ผ่านการตรวจสอบ SHA-256");
                }

                string installPath = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string exePath = Environment.ProcessPath ?? throw new InvalidOperationException("ไม่พบตำแหน่งไฟล์ Launcher");
                string exeName = Path.GetFileName(exePath);
                string processName = Path.GetFileNameWithoutExtension(exeName);
                string scriptPath = Path.Combine(tempRoot, "apply-update.ps1");
                string script = $@"
$ErrorActionPreference = 'Stop'
$install = '{EscapePowerShell(installPath)}'
$zip = '{EscapePowerShell(zipPath)}'
$stage = '{EscapePowerShell(extractPath)}'
$processName = '{EscapePowerShell(processName)}'
$exeName = '{EscapePowerShell(exeName)}'
while (Get-Process -Name $processName -ErrorAction SilentlyContinue) {{
    Start-Sleep -Seconds 1
}}
Expand-Archive -LiteralPath $zip -DestinationPath $stage -Force
Copy-Item -Path (Join-Path $stage '*') -Destination $install -Recurse -Force
$exe = Join-Path $install $exeName
if (-not (Test-Path -LiteralPath $exe)) {{
    throw ""Updated executable not found: $exe""
}}
Start-Process -FilePath $exe
";
                await File.WriteAllTextAsync(scriptPath, script, new System.Text.UTF8Encoding(false));

                var startInfo = new ProcessStartInfo { FileName = "powershell.exe", UseShellExecute = false, CreateNoWindow = true };
                startInfo.ArgumentList.Add("-NoProfile");
                startInfo.ArgumentList.Add("-ExecutionPolicy");
                startInfo.ArgumentList.Add("Bypass");
                startInfo.ArgumentList.Add("-File");
                startInfo.ArgumentList.Add(scriptPath);
                Process.Start(startInfo);

                MessageBox.Show($"พบ Launcher เวอร์ชันใหม่ {latestVersion}\nกำลังติดตั้งอัปเดตและเปิด Launcher ใหม่",
                    "HSHONAD Launcher Update", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Application.Exit();
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("ตรวจสอบ/ติดตั้งอัปเดต Launcher ไม่สำเร็จ จึงเปิดเวอร์ชันปัจจุบันต่อไป\n\n" + ex.Message,
                    "HSHONAD Launcher Update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
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
