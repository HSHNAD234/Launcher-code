using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HSHONAD_Launcher
{
    internal static class LauncherUpdateService
    {
        private const string ReleasePathPrefix = "/HSHNAD234/Launcher-code/releases/download/";
        private const string AssetName = "HSHONAD-Launcher-win-x64.zip";

        public static async Task InstallAsync(string downloadUrl, string version, string? sha256)
        {
            if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
                !uri.AbsolutePath.StartsWith(ReleasePathPrefix, StringComparison.Ordinal) ||
                !uri.AbsolutePath.EndsWith("/" + AssetName, StringComparison.Ordinal))
                throw new InvalidOperationException("URL ดาวน์โหลดไม่ใช่ GitHub Release ของ HSHONAD Launcher");

            if (string.IsNullOrWhiteSpace(version) || version.Length > 40 ||
                version.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidOperationException("หมายเลขเวอร์ชันไม่ถูกต้อง");

            if (!string.IsNullOrWhiteSpace(sha256) &&
                !System.Text.RegularExpressions.Regex.IsMatch(sha256, "^[a-fA-F0-9]{64}$"))
                throw new InvalidOperationException("ค่า SHA-256 ไม่ถูกต้อง");

            string exePath = Application.ExecutablePath;
            string installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            string scriptPath = Path.Combine(Path.GetTempPath(), "hshonad-update-" + Guid.NewGuid().ToString("N") + ".ps1");
            string logPath = Path.Combine(Path.GetTempPath(), "hshonad-update-" + Guid.NewGuid().ToString("N") + ".log");
            int processId = Environment.ProcessId;
            static string PsQuote(string value) => "'" + value.Replace("'", "''") + "'";
            string expectedHash = string.IsNullOrWhiteSpace(sha256) ? "" : sha256.ToLowerInvariant();

            string script = $@"
$ErrorActionPreference = 'Stop'
$log = {PsQuote(logPath)}
try {{
  while (Get-Process -Id {processId} -ErrorAction SilentlyContinue) {{ Start-Sleep -Milliseconds 500 }}
  $url = {PsQuote(uri.AbsoluteUri)}
  $installDir = {PsQuote(installDir)}
  $exePath = {PsQuote(exePath)}
  $expectedHash = {PsQuote(expectedHash)}
  $zipPath = Join-Path $env:TEMP ('hshonad-update-' + [guid]::NewGuid().ToString('N') + '.zip')
  $extractDir = Join-Path $env:TEMP ('hshonad-extract-' + [guid]::NewGuid().ToString('N'))
  Invoke-WebRequest -Uri $url -OutFile $zipPath -UseBasicParsing
  if ($expectedHash) {{
    $actualHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $expectedHash) {{ throw 'SHA-256 verification failed' }}
  }}
  Expand-Archive -LiteralPath $zipPath -DestinationPath $extractDir -Force
  $files = Get-ChildItem -LiteralPath $extractDir -Force
  if (-not $files) {{ throw 'The update archive is empty' }}
  foreach ($item in $files) {{ Copy-Item -LiteralPath $item.FullName -Destination $installDir -Recurse -Force }}
  Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue
  Remove-Item -LiteralPath $extractDir -Recurse -Force -ErrorAction SilentlyContinue
  Start-Process -FilePath $exePath -WorkingDirectory $installDir
  Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
}} catch {{
  $_ | Out-String | Add-Content -LiteralPath $log
  Add-Content -LiteralPath $log ('Update failed. Log: ' + $log)
  Start-Process notepad.exe -ArgumentList $log
}}
";
            await File.WriteAllTextAsync(scriptPath, script, System.Text.Encoding.UTF8);
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File " + QuoteArgument(scriptPath),
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = installDir
            });
            Application.Exit();
        }

        private static string QuoteArgument(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}