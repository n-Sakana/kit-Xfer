using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Xfer
{
    // Used on Linux only. The managed HttpListener there answers a request only when its Host header names one of
    // the listener's prefixes, but tailscale serve hands the phone's requests over with the tailnet Host
    // (device.tailnet.ts.net:10000). The relay owns Xfer's loopback port, passes each request on to the listener
    // (on another loopback port) under the listener's own name, and keeps the original Host in X-Xfer-Host for the
    // request role. One request per connection. Like the listener on Windows, it listens on 127.0.0.1 only.
    internal sealed class LoopbackRelay : IDisposable
    {
        public const string OriginalHostHeader = "X-Xfer-Host";
        private const int MaxHeadBytes = 64 * 1024;
        private static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        private readonly TcpListener _listener;
        private int _target;

        public LoopbackRelay(int port)
        {
            _listener = new TcpListener(IPAddress.Loopback, port);
        }

        // A free loopback port for the listener behind the relay.
        public static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            try
            {
                return ((IPEndPoint)probe.LocalEndpoint).Port;
            }
            finally
            {
                probe.Stop();
            }
        }

        // Takes Xfer's port now, so a second Xfer on the same port fails here as it would with the listener.
        public void Start(int target)
        {
            _target = target;
            _listener.Start();
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            using (cancellationToken.Register(Stop))
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    TcpClient client;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync();
                    }
                    catch (Exception)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }

                        throw;
                    }

                    var accepted = client;
                    var ignored = Task.Run(async delegate
                    {
                        try
                        {
                            await RelayAsync(accepted);
                        }
                        catch (Exception exception)
                        {
                            Diagnostics.Failure("http.loopback", "", exception);
                        }
                    });
                }
            }
        }

        private async Task RelayAsync(TcpClient client)
        {
            using (client)
            using (var upstream = new TcpClient(AddressFamily.InterNetwork))
            {
                client.NoDelay = true;
                var down = client.GetStream();
                var data = new byte[MaxHeadBytes];
                int filled = 0, headEnd = -1;
                while (headEnd < 0)
                {
                    if (filled == data.Length)
                    {
                        return;
                    }

                    int read = await down.ReadAsync(data, filled, data.Length - filled);
                    if (read <= 0)
                    {
                        return;
                    }

                    filled += read;
                    headEnd = HeadEnd(data, filled);
                }

                string head = Rewrite(Latin1.GetString(data, 0, headEnd), _target);
                if (head == null)
                {
                    return;
                }

                await upstream.ConnectAsync(IPAddress.Loopback, _target);
                upstream.NoDelay = true;
                var up = upstream.GetStream();
                var headBytes = Latin1.GetBytes(head);
                await up.WriteAsync(headBytes, 0, headBytes.Length);
                if (filled > headEnd)
                {
                    await up.WriteAsync(data, headEnd, filled - headEnd);
                }

                // The rest of the request body goes on while the response comes back; the listener closes the
                // connection after the response (Connection: close), and closing both ends ends the request copy.
                var body = CopyAsync(down, up);
                await CopyAsync(up, down);
            }
        }

        private static async Task CopyAsync(NetworkStream from, NetworkStream to)
        {
            var buffer = new byte[64 * 1024];
            try
            {
                int read;
                while ((read = await from.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await to.WriteAsync(buffer, 0, read);
                }
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        // The index just after the blank line that ends the request head, or -1.
        private static int HeadEnd(byte[] data, int length)
        {
            for (int i = 3; i < length; i++)
            {
                if (data[i] == '\n' && data[i - 1] == '\r' && data[i - 2] == '\n' && data[i - 3] == '\r')
                {
                    return i + 1;
                }
            }

            return -1;
        }

        // The request head for the listener: its own Host, the original Host (empty when there was none) and
        // Connection: close. A head the relay cannot read is dropped.
        internal static string Rewrite(string head, int target)
        {
            var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length < 2 || lines[0].Length == 0)
            {
                return null;
            }

            var result = new StringBuilder();
            result.Append(lines[0]).Append("\r\n");
            string host = null;
            bool keep = false;
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length == 0)
                {
                    continue;
                }

                if (line[0] == ' ' || line[0] == '\t')
                {
                    // An old-style continuation line belongs to the header before it.
                    if (keep)
                    {
                        result.Append(line).Append("\r\n");
                    }

                    continue;
                }

                int colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    return null;
                }

                string name = line.Substring(0, colon).Trim();
                if (string.Equals(name, "Host", StringComparison.OrdinalIgnoreCase))
                {
                    if (host != null)
                    {
                        return null;
                    }

                    host = line.Substring(colon + 1).Trim();
                    keep = false;
                    continue;
                }

                keep = !string.Equals(name, "Connection", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(name, "Keep-Alive", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(name, "Proxy-Connection", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(name, OriginalHostHeader, StringComparison.OrdinalIgnoreCase);
                if (keep)
                {
                    result.Append(line).Append("\r\n");
                }
            }

            result.Append("Host: 127.0.0.1:").Append(target).Append("\r\n");
            result.Append(OriginalHostHeader).Append(": ").Append(host ?? string.Empty).Append("\r\n");
            result.Append("Connection: close\r\n\r\n");
            return result.ToString();
        }

        private void Stop()
        {
            try
            {
                _listener.Stop();
            }
            catch (SocketException)
            {
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
