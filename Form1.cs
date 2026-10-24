
using System;
using System.Diagnostics;
using System.IO;
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
        private string _backendBaseUrl =
            "https://hshonad-backend.onrender.com";

        private static readonly HttpClient Http =
            new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        private CancellationTokenSource? _loginCts;
        private LocalProxyServer? _proxy;

        public Form1() => InitializeComponent();

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
                    case "configureApis":
                        var configuredApi = GetString(doc.RootElement, "launcherApi", "");
                        if (!string.IsNullOrWhiteSpace(configuredApi) &&
                            Uri.TryCreate(configuredApi, UriKind.Absolute, out var configuredUri) &&
                            (configuredUri.Scheme == Uri.UriSchemeHttps || configuredUri.IsLoopback && configuredUri.Scheme == Uri.UriSchemeHttp) &&
                            string.IsNullOrEmpty(configuredUri.UserInfo) &&
                            string.IsNullOrEmpty(configuredUri.Query) &&
                            string.IsNullOrEmpty(configuredUri.Fragment))
                        {
                            _backendBaseUrl = configuredUri.ToString().TrimEnd('/');
                        }
                        break;

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

                    case "installLauncherUpdate":
                        await SendToPageAsync(new
                        {
                            type = "launcherUpdateStatus",
                            status = "installing",
                            message = "กำลังเตรียมติดตั้งอัปเดต..."
                        });
                        await LauncherUpdateService.InstallAsync(
                            GetString(doc.RootElement, "download_url", ""),
                            GetString(doc.RootElement, "version", ""),
                            GetString(doc.RootElement, "sha256", ""));
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

        private static bool HasNonEmptyQueryParameter(Uri uri, string parameterName)
        {
            foreach (string part in uri.Query.TrimStart('?').Split('&'))
            {
                if (string.IsNullOrWhiteSpace(part))
                    continue;

                string[] pair = part.Split(new[] { '=' }, 2);
                string name = Uri.UnescapeDataString(pair[0].Replace("+", " "));
                if (!string.Equals(name, parameterName, StringComparison.Ordinal))
                    continue;

                return pair.Length == 2 &&
                       !string.IsNullOrWhiteSpace(
                           Uri.UnescapeDataString(pair[1].Replace("+", " ")));
            }

            return false;
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
                    _backendBaseUrl + "/api/launcher/device-sessions",
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
                    !Uri.TryCreate(_backendBaseUrl, UriKind.Absolute, out var backendUri) ||
                    uri.Scheme != Uri.UriSchemeHttps ||
                    !string.Equals(uri.Host, backendUri.Host, StringComparison.OrdinalIgnoreCase) ||
                    uri.Port != backendUri.Port ||
                    !string.Equals(uri.AbsolutePath, "/auth/discord", StringComparison.Ordinal) ||
                    !string.IsNullOrEmpty(uri.UserInfo) ||
                    !string.IsNullOrEmpty(uri.Fragment) ||
                    !HasNonEmptyQueryParameter(uri, "state"))
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
                        _backendBaseUrl + "/api/launcher/device-sessions/" +
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

                var proxy = new LocalProxyServer(8888);
                proxy.Start();
                _proxy = proxy;

                await SendProxyStatusAsync(
                    "running", "เปิด Proxy สำเร็จ: 127.0.0.1:8888");
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
