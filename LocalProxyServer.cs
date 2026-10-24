
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HSHONAD_Launcher
{
    public sealed class LocalProxyServer : IAsyncDisposable
    {
        private readonly int _port;
        private readonly HashSet<string> _allowedHosts;
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _acceptTask;

        public bool IsRunning => _listener != null;

        public LocalProxyServer(
            int port = 8888,
            IEnumerable<string>? allowedHosts = null)
        {
            if (port < 1024 || port > 65535)
                throw new ArgumentOutOfRangeException(nameof(port));

            _port = port;
            _allowedHosts = new HashSet<string>(
                (allowedHosts ?? Array.Empty<string>())
                    .Where(host => !string.IsNullOrWhiteSpace(host))
                    .Select(host => host.Trim().ToLowerInvariant()),
                StringComparer.OrdinalIgnoreCase);
        }

        private bool IsHostAllowed(string host)
        {
            string normalized = host.Trim().TrimEnd('.').ToLowerInvariant();
            foreach (string rule in _allowedHosts)
            {
                if (rule.StartsWith("*.", StringComparison.Ordinal))
                {
                    string suffix = rule[1..];
                    if (normalized.EndsWith(suffix, StringComparison.Ordinal) &&
                        normalized.Length > suffix.Length)
                        return true;
                }
                else if (string.Equals(normalized, rule, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public void Start()
        {
            if (IsRunning)
                return;

            var listener = new TcpListener(IPAddress.Loopback, _port);
            listener.Start();

            _listener = listener;
            _cts = new CancellationTokenSource();
            _acceptTask = AcceptLoopAsync(listener, _cts.Token);
        }

        private async Task AcceptLoopAsync(
            TcpListener listener, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    TcpClient client =
                        await listener.AcceptTcpClientAsync(token);

                    _ = HandleClientAsync(client, token);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (SocketException) when (token.IsCancellationRequested) { }
        }

        private static async Task HandleClientAsync(
            TcpClient client, CancellationToken token)
        {
            using (client)
            {
                try
                {
                    using NetworkStream stream = client.GetStream();
                    string? header = await ReadHeaderAsync(stream, token);

                    if (string.IsNullOrWhiteSpace(header))
                        return;

                    string[] lines = header.Split("\r\n");
                    string[] request = lines[0].Split(' ', 3);

                    if (request.Length != 3)
                    {
                        await SendErrorAsync(stream, 400, "Bad Request");
                        return;
                    }

                    if (!request[0].Equals(
                        "CONNECT", StringComparison.OrdinalIgnoreCase))
                    {
                        await SendErrorAsync(
                            stream, 501,
                            "Only HTTPS CONNECT tunneling is supported.");
                        return;
                    }

                    string authority = request[1];

                    if (!Uri.TryCreate(
                            "tcp://" + authority,
                            UriKind.Absolute,
                            out Uri? target) ||
                        target is null ||
                        string.IsNullOrWhiteSpace(target.Host) ||
                        target.Port != 443 ||
                        !IsHostAllowed(target.Host))
                    {
                        await SendErrorAsync(
                            stream, 403,
                            "Target not allowed. Configure an exact host or *.domain rule in HSHONAD Launcher Settings.");
                        return;
                    }

                    using var upstream = new TcpClient();
                    await upstream.ConnectAsync(
                        target.Host, target.Port, token);

                    using NetworkStream upstreamStream =
                        upstream.GetStream();

                    byte[] response = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 Connection Established\r\n\r\n");

                    await stream.WriteAsync(response, token);

                    using var linked =
                        CancellationTokenSource.CreateLinkedTokenSource(token);

                    Task a = CopyAsync(
                        stream, upstreamStream, linked.Token);

                    Task b = CopyAsync(
                        upstreamStream, stream, linked.Token);

                    await Task.WhenAny(a, b);
                    linked.Cancel();

                    try
                    {
                        await Task.WhenAll(a, b);
                    }
                    catch (OperationCanceledException) { }
                    catch (IOException) { }
                    catch (SocketException) { }
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
                catch (SocketException) { }
                catch (ArgumentException) { }
            }
        }

        private static async Task<string?> ReadHeaderAsync(
            NetworkStream stream, CancellationToken token)
        {
            using var buffer = new MemoryStream();
            byte[] one = new byte[1];
            int matched = 0;
            byte[] ending = { 13, 10, 13, 10 };

            while (buffer.Length < 16 * 1024)
            {
                int count = await stream.ReadAsync(
                    one.AsMemory(0, 1), token);

                if (count == 0)
                    return null;

                byte value = one[0];
                buffer.WriteByte(value);

                if (value == ending[matched])
                {
                    matched++;

                    if (matched == ending.Length)
                        return Encoding.ASCII.GetString(buffer.ToArray());
                }
                else
                {
                    matched = value == ending[0] ? 1 : 0;
                }
            }

            return null;
        }

        private static async Task CopyAsync(
            Stream source, Stream destination, CancellationToken token)
        {
            await source.CopyToAsync(destination, 81920, token);
        }

        private static async Task SendErrorAsync(
            NetworkStream stream, int status, string message)
        {
            byte[] body = Encoding.UTF8.GetBytes(message);

            byte[] header = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status} Proxy Error\r\n" +
                "Connection: close\r\n" +
                "Content-Type: text/plain; charset=utf-8\r\n" +
                $"Content-Length: {body.Length}\r\n\r\n");

            await stream.WriteAsync(header);
            await stream.WriteAsync(body);
        }

        public async Task StopAsync()
        {
            TcpListener? listener = _listener;
            CancellationTokenSource? cts = _cts;
            Task? task = _acceptTask;

            _listener = null;
            _cts = null;
            _acceptTask = null;

            cts?.Cancel();
            listener?.Stop();

            if (task != null)
            {
                try { await task; }
                catch (OperationCanceledException) { }
            }

            cts?.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
        }
    }
}
