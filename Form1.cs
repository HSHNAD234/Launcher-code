
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace HSHONAD_Launcher
{
    public partial class Form1 : Form
    {
        private const string BackendBaseUrl =
            "https://hshonad-backend.onrender.com";

        private static readonly HttpClient Http =
            new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        private CancellationTokenSource? _loginCts;
        private LocalProxyServer? _proxy;
        private string? _gameExecutablePath;
        private readonly System.Windows.Forms.Timer _gameMonitorTimer =
            new System.Windows.Forms.Timer { Interval = 2500 };
        private bool _lastGameRunning;
        private bool _monitorTickBusy;
        private bool _lowPowerActive;
        private ProcessPriorityClass? _originalPriority;

        private string UserDataDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HSHONAD", "Launcher");

        private string GamePathFile => Path.Combine(UserDataDirectory, "game-path.txt");
        private string ProxyAllowlistFile => Path.Combine(UserDataDirectory, "proxy-allowlist.txt");

        public Form1()
        {
            InitializeComponent();
            _gameMonitorTimer.Tick += GameMonitorTimer_Tick;
        }

        private async void Form1_Load(object? sender, EventArgs e)
        {
            try
            {
                string htmlPath = Path.Combine(
                    AppContext.BaseDirectory, "wwwroot", "index.html");

                if (!File.Exists(htmlPath))
                {
                    string sourcePath = Path.Combine(
                        Directory.GetCurrentDirectory(),
                        "wwwroot", "index.html");

                    if (File.Exists(sourcePath))
                        htmlPath = sourcePath;
                }

                if (!File.Exists(htmlPath))
                {
                    MessageBox.Show(
                        "ไม่พบไฟล์ index.html\n\n" + htmlPath,
                        "HSHONAD Launcher",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    return;
                }

                await webView.EnsureCoreWebView2Async();

                webView.CoreWebView2.WebMessageReceived +=
                    CoreWebView2_WebMessageReceived;

                _gameExecutablePath = LoadSavedGamePath();
                webView.CoreWebView2.NavigationCompleted += async (_, _) =>
                {
                    await SendGameFolderStatusAsync("พร้อมใช้งาน");
                    await SendToPageAsync(new
                    {
                        type = "proxyAllowlist",
                        hosts = string.Join(Environment.NewLine, LoadProxyAllowedHosts())
                    });
                    await RefreshGameStateAsync();
                };

                _gameMonitorTimer.Start();
                webView.Source = new Uri(htmlPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "เปิด Launcher ไม่สำเร็จ\n\n" + ex.Message,
                    "HSHONAD Launcher",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private async void CoreWebView2_WebMessageReceived(
            object? sender,
            CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                using var doc = JsonDocument.Parse(e.WebMessageAsJson);

                if (!doc.RootElement.TryGetProperty("type", out var typeEl))
                    return;

                string type = typeEl.GetString() ?? "";

                switch (type)
                {
                    case "discordWhitelistLogin":
                        _loginCts?.Cancel();
                        _loginCts?.Dispose();
                        _loginCts = new CancellationTokenSource();
                        await StartDiscordLoginAsync(_loginCts.Token);
                        break;

                    case "logout":
                        _loginCts?.Cancel();
                        await SendToPageAsync(new
                        {
                            type = "discordStatus",
                            status = "logged_out"
                        });
                        break;

                    case "startProxy":
                        await StartProxyAsync();
                        break;

                    case "stopProxy":
                        await StopProxyAsync();
                        break;

                    case "selectGame":
                        SelectGameExecutable();
                        break;

                    case "scanGame":
                        await ScanGameAsync();
                        break;

                    case "launchGame":
                        await LaunchGameAsync();
                        break;

                    case "saveProxyHosts":
                        await SaveProxyAllowlistAsync(
                            doc.RootElement.TryGetProperty("hosts", out var hostsEl)
                                ? hostsEl.GetString() ?? ""
                                : "");
                        break;
                }
            }
            catch (Exception ex)
            {
                await SendToPageAsync(new
                {
                    type = "discordStatus",
                    status = "error",
                    message = "ดำเนินการไม่สำเร็จ: " + ex.Message
                });
            }
        }

        private async Task StartDiscordLoginAsync(CancellationToken token)
        {
            await SendToPageAsync(new
            {
                type = "discordStatus",
                status = "pending",
                message = "กำลังเชื่อมต่อ Discord...",
                whitelistStatus = "กำลังตรวจสอบ..."
            });

            try
            {
                using var response = await Http.PostAsync(
                    BackendBaseUrl + "/api/launcher/device-sessions",
                    new StringContent("{}", Encoding.UTF8, "application/json"),
                    token);

                string body = await response.Content.ReadAsStringAsync(token);

                if (!response.IsSuccessStatusCode)
                {
                    throw new Exception(
                        $"Backend HTTP {(int)response.StatusCode}: {body}");
                }

                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                string sessionId =
                    root.GetProperty("device_session_id").GetString()
                    ?? throw new Exception("ไม่มี session ID");

                string secret =
                    root.GetProperty("device_secret").GetString()
                    ?? throw new Exception("ไม่มี session secret");

                string verifyUrl =
                    root.GetProperty("verification_url").GetString()
                    ?? throw new Exception("ไม่มี OAuth URL");

                if (!Uri.TryCreate(verifyUrl, UriKind.Absolute, out var uri) ||
                    uri.Scheme != Uri.UriSchemeHttps ||
                    uri.Host != "discord.com")
                {
                    throw new Exception("URL สำหรับ Login ไม่ถูกต้อง");
                }

                Process.Start(new ProcessStartInfo(verifyUrl)
                {
                    UseShellExecute = true
                });

                for (int i = 0; i < 100; i++)
                {
                    token.ThrowIfCancellationRequested();

                    await Task.Delay(TimeSpan.FromSeconds(3), token);

                    using var request = new HttpRequestMessage(
                        HttpMethod.Get,
                        BackendBaseUrl + "/api/launcher/device-sessions/" +
                        Uri.EscapeDataString(sessionId));

                    request.Headers.Authorization =
                        new AuthenticationHeaderValue("Bearer", secret);

                    using var pollResponse = await Http.SendAsync(request, token);

                    if (!pollResponse.IsSuccessStatusCode)
                        continue;

                    string pollBody =
                        await pollResponse.Content.ReadAsStringAsync(token);

                    using var pollDoc = JsonDocument.Parse(pollBody);
                    var data = pollDoc.RootElement;

                    string status = data.TryGetProperty("status", out var statusEl)
                        ? statusEl.GetString() ?? "pending"
                        : "pending";

                    if (status == "approved")
                    {
                        await SendToPageAsync(new
                        {
                            type = "discordStatus",
                            status = "approved",
                            username = GetString(data, "username", "Discord User"),
                            discordId = GetString(data, "discord_user_id", ""),
                            avatarUrl = GetString(data, "avatar_url", ""),
                            whitelistStatus = "Approved",
                            message = "ตรวจสอบ Whitelist ผ่านแล้ว"
                        });
                        return;
                    }

                    if (status == "denied")
                    {
                        await SendToPageAsync(new
                        {
                            type = "discordStatus",
                            status = "denied",
                            whitelistStatus = "Not whitelisted",
                            message = "บัญชี Discord นี้ไม่มีสิทธิ์ Whitelist"
                        });
                        return;
                    }

                    if (status == "expired" || status == "error")
                    {
                        await SendToPageAsync(new
                        {
                            type = "discordStatus",
                            status = "error",
                            message = "เซสชันหมดอายุหรือ Backend เกิดข้อผิดพลาด"
                        });
                        return;
                    }
                }

                await SendToPageAsync(new
                {
                    type = "discordStatus",
                    status = "error",
                    message = "หมดเวลารอ Discord Login กรุณาลองใหม่"
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                await SendToPageAsync(new
                {
                    type = "discordStatus",
                    status = "error",
                    message = "Login ไม่สำเร็จ: " + ex.Message
                });
            }
        }

        private string? LoadSavedGamePath()
        {
            try
            {
                if (!File.Exists(GamePathFile))
                    return null;

                string path = File.ReadAllText(GamePathFile).Trim();
                return File.Exists(path) ? Path.GetFullPath(path) : path;
            }
            catch
            {
                return null;
            }
        }

        private void SelectGameExecutable()
        {
            using var dialog = new OpenFileDialog
            {
                Title = "เลือกไฟล์เกม",
                Filter = "Game executable (*.exe)|*.exe|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false,
                InitialDirectory = !string.IsNullOrWhiteSpace(_gameExecutablePath) &&
                                   Directory.Exists(Path.GetDirectoryName(_gameExecutablePath))
                    ? Path.GetDirectoryName(_gameExecutablePath)!
                    : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
            };

            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;

            try
            {
                Directory.CreateDirectory(UserDataDirectory);
                _gameExecutablePath = Path.GetFullPath(dialog.FileName);
                File.WriteAllText(GamePathFile, _gameExecutablePath);
                _lastGameRunning = false;
                _ = SendGameFolderStatusAsync("บันทึกตำแหน่งเกมแล้ว");
                _ = SendToPageAsync(new
                {
                    type = "launcherStatus",
                    message = "เลือกเกมแล้ว: " + Path.GetFileName(_gameExecutablePath)
                });
            }
            catch (Exception ex)
            {
                _ = SendToPageAsync(new
                {
                    type = "gameFolderStatus",
                    status = "error",
                    path = _gameExecutablePath ?? "",
                    message = "บันทึกตำแหน่งเกมไม่สำเร็จ: " + ex.Message
                });
            }
        }

        private async Task SendGameFolderStatusAsync(string message)
        {
            await SendToPageAsync(new
            {
                type = "gameFolderStatus",
                status = !string.IsNullOrWhiteSpace(_gameExecutablePath) &&
                         File.Exists(_gameExecutablePath) ? "selected" : "empty",
                path = _gameExecutablePath ?? "",
                message
            });
        }

        private async Task ScanGameAsync()
        {
            string? exePath = _gameExecutablePath ?? LoadSavedGamePath();

            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                await SendToPageAsync(new
                {
                    type = "gameScanStatus",
                    status = "error",
                    message = "ยังไม่พบไฟล์เกม กรุณาใช้ Select Game เพื่อเลือกไฟล์ .exe",
                    summary = "ไม่พบไฟล์เกมที่เลือก"
                });
                return;
            }

            try
            {
                var exeInfo = new FileInfo(exePath);
                string directory = Path.GetDirectoryName(exePath)
                    ?? throw new IOException("ไม่พบโฟลเดอร์ของไฟล์เกม");
                int topLevelFiles = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).Count();
                int exeCount = Directory.EnumerateFiles(directory, "*.exe", SearchOption.TopDirectoryOnly).Count();
                string size = exeInfo.Length >= 1024L * 1024
                    ? $"{exeInfo.Length / (1024d * 1024):F1} MB"
                    : $"{exeInfo.Length / 1024d:F0} KB";

                string summary =
                    $"ไฟล์เกม: {exeInfo.Name}\n" +
                    $"ตำแหน่ง: {directory}\n" +
                    $"ขนาดไฟล์เกม: {size}\n" +
                    $"ไฟล์ในโฟลเดอร์ชั้นบนสุด: {topLevelFiles:N0}\n" +
                    $"ไฟล์ .exe ชั้นบนสุด: {exeCount:N0}\n\n" +
                    "ตรวจสอบเบื้องต้นแล้วเท่านั้น ยังไม่ได้เทียบ SHA-256 กับ Manifest และไม่มีการแก้ไข/ลบไฟล์";

                await SendToPageAsync(new
                {
                    type = "gameScanStatus",
                    status = "completed",
                    path = exePath,
                    message = "ตรวจสอบเบื้องต้นเสร็จแล้ว (อ่านอย่างเดียว)",
                    summary
                });
            }
            catch (Exception ex)
            {
                await SendToPageAsync(new
                {
                    type = "gameScanStatus",
                    status = "error",
                    message = "อ่านข้อมูลโฟลเดอร์เกมไม่ได้: " + ex.Message,
                    summary = "ตรวจสอบไม่สำเร็จ"
                });
            }
        }

        private async Task LaunchGameAsync()
        {
            string? exePath = _gameExecutablePath ?? LoadSavedGamePath();
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                await SendToPageAsync(new
                {
                    type = "launcherStatus",
                    message = "กรุณาเลือกไฟล์เกมก่อน"
                });
                return;
            }

            try
            {
                if (IsSelectedGameRunning())
                {
                    await SendToPageAsync(new
                    {
                        type = "launcherStatus",
                        message = "ตรวจพบว่าเกมกำลังทำงานอยู่แล้ว"
                    });
                    await RefreshGameStateAsync();
                    return;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = Path.GetDirectoryName(exePath)!,
                    UseShellExecute = true
                };

                Process.Start(startInfo);
                await SendToPageAsync(new
                {
                    type = "launcherStatus",
                    message = "ส่งคำสั่งเปิดเกมแล้ว กำลังติดตามสถานะ"
                });
                await Task.Delay(400);
                await RefreshGameStateAsync();
            }
            catch (Exception ex)
            {
                await SendToPageAsync(new
                {
                    type = "launcherStatus",
                    message = "เปิดเกมไม่สำเร็จ: " + ex.Message
                });
            }
        }

        private async void GameMonitorTimer_Tick(object? sender, EventArgs e)
        {
            if (_monitorTickBusy || IsDisposed)
                return;

            _monitorTickBusy = true;
            try
            {
                await RefreshGameStateAsync();
            }
            finally
            {
                _monitorTickBusy = false;
            }
        }

        private async Task RefreshGameStateAsync()
        {
            bool running = IsSelectedGameRunning();

            if (running == _lastGameRunning)
                return;

            _lastGameRunning = running;

            if (running)
            {
                EnableLowPowerMode();

                // Start the local proxy only when a game process is detected and
                // the user has explicitly configured allowed destination hosts.
                if (_proxy?.IsRunning != true && LoadProxyAllowedHosts().Count > 0)
                    await StartProxyAsync();

                await SendToPageAsync(new
                {
                    type = "gameStatus",
                    status = "running",
                    running = true,
                    lowPower = true,
                    gamePath = _gameExecutablePath ?? "",
                    message = "ตรวจพบเกมกำลังทำงาน: เปิด Low Power Mode ให้ Launcher"
                });
            }
            else
            {
                DisableLowPowerMode();

                if (_proxy?.IsRunning == true)
                    await StopProxyAsync();

                await SendToPageAsync(new
                {
                    type = "gameStatus",
                    status = "stopped",
                    running = false,
                    lowPower = false,
                    gamePath = _gameExecutablePath ?? "",
                    message = "เกมไม่ได้ทำงาน: ปิด Proxy อัตโนมัติและคืนโหมดปกติ"
                });
            }
        }

        private bool IsSelectedGameRunning()
        {
            string? selected = _gameExecutablePath ?? LoadSavedGamePath();
            if (string.IsNullOrWhiteSpace(selected))
                return false;

            string name = Path.GetFileNameWithoutExtension(selected);
            if (string.IsNullOrWhiteSpace(name))
                return false;

            foreach (Process process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        string? actualPath = process.MainModule?.FileName;
                        if (string.IsNullOrWhiteSpace(actualPath))
                            continue;

                        if (string.Equals(
                            Path.GetFullPath(actualPath),
                            Path.GetFullPath(selected),
                            StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    catch
                    {
                        // หาก Windows ปฏิเสธการอ่าน MainModule ของโปรเซสชื่อเดียวกัน
                        // อย่าตัดสินว่าเกมปิดทันที เพราะอาจทำให้ Proxy ถูกปิดระหว่างเล่น
                        return true;
                    }
                }
            }

            return false;
        }

        private void EnableLowPowerMode()
        {
            if (_lowPowerActive)
                return;

            try
            {
                using Process current = Process.GetCurrentProcess();
                _originalPriority = current.PriorityClass;
                current.PriorityClass = ProcessPriorityClass.BelowNormal;
                _lowPowerActive = true;
            }
            catch
            {
                // Low Power เป็น best-effort; เกมต้องไม่ถูกหยุดเพราะเปลี่ยน Priority ไม่ได้
            }
        }

        private void DisableLowPowerMode()
        {
            if (!_lowPowerActive)
                return;

            try
            {
                if (_originalPriority.HasValue)
                {
                    using Process current = Process.GetCurrentProcess();
                    current.PriorityClass = _originalPriority.Value;
                }
            }
            catch { }
            finally
            {
                _lowPowerActive = false;
                _originalPriority = null;
            }
        }

        private IReadOnlyList<string> LoadProxyAllowedHosts()
        {
            try
            {
                if (!File.Exists(ProxyAllowlistFile))
                    return Array.Empty<string>();

                return File.ReadAllLines(ProxyAllowlistFile)
                    .Select(line => line.Trim().ToLowerInvariant())
                    .Where(line => line.Length > 0 && !line.StartsWith("#"))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private async Task SaveProxyAllowlistAsync(string input)
        {
            try
            {
                var hosts = input.Split(new[] { '\r', '\n', ',', ';' },
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(host => host.ToLowerInvariant())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                foreach (string host in hosts)
                {
                    string candidate = host.StartsWith("*.", StringComparison.Ordinal)
                        ? host[2..]
                        : host;

                    if (candidate.Length == 0 || Uri.CheckHostName(candidate) == UriHostNameType.Unknown)
                        throw new InvalidOperationException("Host ไม่ถูกต้อง: " + host);
                }

                Directory.CreateDirectory(UserDataDirectory);
                File.WriteAllLines(ProxyAllowlistFile, hosts);

                await SendToPageAsync(new
                {
                    type = "proxyAllowlist",
                    hosts = string.Join(Environment.NewLine, hosts),
                    message = hosts.Length == 0
                        ? "ยังไม่ได้ตั้งค่า Host ที่อนุญาต Proxy จึงจะไม่ยอมรับปลายทางใด ๆ"
                        : $"บันทึก Host ที่อนุญาต {hosts.Length} รายการแล้ว"
                });
            }
            catch (Exception ex)
            {
                await SendToPageAsync(new
                {
                    type = "launcherStatus",
                    message = "บันทึก Proxy allowlist ไม่สำเร็จ: " + ex.Message
                });
            }
        }

        private async Task StartProxyAsync()
        {
            try
            {
                if (_proxy?.IsRunning == true)
                {
                    await SendProxyStatusAsync(
                        "running", "Proxy เปิดทำงานอยู่แล้ว");
                    return;
                }

                if (!IsSelectedGameRunning())
                {
                    await SendProxyStatusAsync(
                        "stopped", "Proxy จะเปิดเฉพาะระหว่างตรวจพบเกมที่เลือกกำลังทำงาน");
                    return;
                }

                var allowedHosts = LoadProxyAllowedHosts();
                if (allowedHosts.Count == 0)
                {
                    await SendProxyStatusAsync(
                        "error", "ยังไม่มี Proxy allowlist กรุณาใส่ Host ที่อนุญาตใน Settings ก่อน");
                    return;
                }

                var proxy = new LocalProxyServer(8888, allowedHosts);
                proxy.Start();
                _proxy = proxy;

                await SendProxyStatusAsync(
                    "running", "HSHONAD Proxy ทำงานที่ 127.0.0.1:8888 (HTTPS CONNECT เฉพาะ Host ที่อนุญาต)");
            }
            catch (Exception ex)
            {
                await SendProxyStatusAsync(
                    "error", "เปิด Proxy ไม่สำเร็จ: " + ex.Message);
            }
        }

        private async Task StopProxyAsync()
        {
            if (_proxy != null)
            {
                await _proxy.DisposeAsync();
                _proxy = null;
            }

            await SendProxyStatusAsync("stopped", "ปิด Proxy แล้ว");
        }

        private Task SendProxyStatusAsync(string status, string message)
        {
            return SendToPageAsync(new
            {
                type = "proxyStatus",
                status,
                message,
                address = status == "running" ? "127.0.0.1:8888" : null
            });
        }

        private static string GetString(
            JsonElement element, string property, string fallback) =>
            element.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? fallback
                : fallback;

        private async Task SendToPageAsync(object message)
        {
            if (IsDisposed || Disposing || webView.CoreWebView2 == null)
                return;

            string json = JsonSerializer.Serialize(message);
            webView.CoreWebView2.PostWebMessageAsJson(json);
            await Task.CompletedTask;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _loginCts?.Cancel();
            _loginCts?.Dispose();
            _gameMonitorTimer.Stop();
            _gameMonitorTimer.Dispose();
            DisableLowPowerMode();

            // การปิด listener จะเกิดขึ้นเมื่อฟอร์มกำลังปิด
            if (_proxy != null)
            {
                _proxy.DisposeAsync().AsTask().GetAwaiter().GetResult();
                _proxy = null;
            }

            base.OnFormClosing(e);
        }
    }
}
