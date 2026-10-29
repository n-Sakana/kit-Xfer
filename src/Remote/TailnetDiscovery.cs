using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace Xfer
{
    internal sealed class TailnetDiscovery
    {
        public const int ServePort = 10000;
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(8);

        private readonly object _gate = new object();
        private List<TailnetDevice> _cached;
        private DateTime _cachedUntilUtc;

        public async Task<List<TailnetDevice>> ReadAsync()
        {
            lock (_gate)
            {
                if (_cached != null && DateTime.UtcNow < _cachedUntilUtc)
                {
                    return new List<TailnetDevice>(_cached);
                }
            }

            var discovered = await Task.Run((Func<List<TailnetDevice>>)Discover);
            lock (_gate)
            {
                _cached = discovered;
                _cachedUntilUtc = DateTime.UtcNow + CacheLifetime;
                return new List<TailnetDevice>(_cached);
            }
        }

        public static bool TryConfigureServe(int localPort, out string url, out string error)
        {
            url = null;
            error = null;
            if (localPort != AppOptions.DefaultPort)
            {
                return false;
            }

            string output;
            string commandError;
            int exitCode;
            if (!TryRunTailscale(
                    string.Format(
                        "serve --bg --yes --https={0} http://127.0.0.1:{1}",
                        ServePort,
                        localPort),
                    6000,
                    out output,
                    out commandError,
                    out exitCode))
            {
                error = commandError;
                return false;
            }
            if (exitCode != 0)
            {
                error = string.IsNullOrWhiteSpace(commandError) ? output.Trim() : commandError.Trim();
                return false;
            }

            try
            {
                var status = ReadTailscaleStatus();
                var self = ReadObject(status, "Self");
                var dnsName = ReadString(self, "DNSName");
                if (!string.IsNullOrWhiteSpace(dnsName))
                {
                    url = BuildUrl(dnsName);
                }
            }
            catch (Exception exception)
            {
                error = exception.Message;
            }
            return true;
        }

        private static List<TailnetDevice> Discover()
        {
            var result = new List<TailnetDevice>();
            Dictionary<string, object> status;
            try
            {
                status = ReadTailscaleStatus();
            }
            catch
            {
                result.Add(new TailnetDevice(
                    Environment.MachineName,
                    string.Empty,
                    string.Empty,
                    PlatformInfo.Name,
                    true));
                return result;
            }

            var self = ReadObject(status, "Self");
            var selfDns = ReadString(self, "DNSName");
            result.Add(new TailnetDevice(
                Environment.MachineName,
                TrimDnsName(selfDns),
                string.IsNullOrWhiteSpace(selfDns) ? string.Empty : BuildUrl(selfDns),
                PlatformInfo.Name,
                true));

            var peerMap = ReadObject(status, "Peer");
            var tasks = new List<Task<TailnetDevice>>();
            if (peerMap != null)
            {
                foreach (var value in peerMap.Values)
                {
                    var peer = value as Dictionary<string, object>;
                    if (peer == null || !ReadBoolean(peer, "Online"))
                    {
                        continue;
                    }
                    var dnsName = ReadString(peer, "DNSName");
                    if (string.IsNullOrWhiteSpace(dnsName))
                    {
                        continue;
                    }
                    tasks.Add(Task.Run(delegate { return Probe(dnsName); }));
                }
            }

            if (tasks.Count > 0)
            {
                try
                {
                    Task.WaitAll(tasks.ToArray());
                }
                catch (AggregateException)
                {
                    // A peer that is online need not be running Xfer.
                }
                foreach (var task in tasks)
                {
                    if (task.Status == TaskStatus.RanToCompletion && task.Result != null)
                    {
                        result.Add(task.Result);
                    }
                }
            }

            result.Sort(delegate (TailnetDevice left, TailnetDevice right)
            {
                if (left.Current != right.Current)
                {
                    return left.Current ? -1 : 1;
                }
                return StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
            });
            return result;
        }

        private static TailnetDevice Probe(string dnsName)
        {
            var url = BuildUrl(dnsName);
            try
            {
#if NET9_0_OR_GREATER
#pragma warning disable SYSLIB0014
#endif
                var request = WebRequest.CreateHttp(url + "api/status");
#if NET9_0_OR_GREATER
#pragma warning restore SYSLIB0014
#endif
                request.Method = "GET";
                request.Accept = "application/json";
                request.AllowAutoRedirect = false;
                request.Proxy = null;
                request.Timeout = 1400;
                request.ReadWriteTimeout = 1400;
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        return null;
                    }
                    using (var stream = response.GetResponseStream())
                    using (var body = new MemoryStream())
                    {
                        if (stream == null)
                        {
                            return null;
                        }
                        stream.CopyTo(body);
                        var status = JsonCodec.ParseObject(body.ToArray());
                        var name = ReadString(status, "device");
                        var platform = ReadString(status, "platform");
                        object capabilities;
                        if (string.IsNullOrWhiteSpace(name)
                            || string.IsNullOrWhiteSpace(platform)
                            || !status.TryGetValue("capabilities", out capabilities)
                            || !(capabilities is Dictionary<string, object>))
                        {
                            return null;
                        }
                        return new TailnetDevice(
                            name,
                            TrimDnsName(dnsName),
                            url,
                            platform,
                            false);
                    }
                }
            }
            catch (WebException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        private static Dictionary<string, object> ReadTailscaleStatus()
        {
            string output;
            string error;
            int exitCode;
            if (!TryRunTailscale("status --json", 4000, out output, out error, out exitCode))
            {
                throw new InvalidOperationException(error);
            }
            if (exitCode != 0 || string.IsNullOrWhiteSpace(output))
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(error) ? "Tailscale is not available." : error.Trim());
            }
            return JsonCodec.ParseObject(Encoding.UTF8.GetBytes(output));
        }

        private static bool TryRunTailscale(
            string arguments,
            int timeoutMilliseconds,
            out string output,
            out string error,
            out int exitCode)
        {
            output = string.Empty;
            error = string.Empty;
            exitCode = -1;
            Process process = null;
            try
            {
                process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = PlatformInfo.IsWindows ? "tailscale.exe" : "tailscale",
                        Arguments = arguments,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };
                if (!process.Start())
                {
                    error = "Tailscale could not be started.";
                    return false;
                }
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(timeoutMilliseconds))
                {
                    process.Kill();
                    error = "Tailscale did not respond in time.";
                    return false;
                }
                output = outputTask.GetAwaiter().GetResult();
                error = errorTask.GetAwaiter().GetResult();
                exitCode = process.ExitCode;
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
            finally
            {
                if (process != null)
                {
                    process.Dispose();
                }
            }
        }

        private static Dictionary<string, object> ReadObject(
            Dictionary<string, object> source,
            string name)
        {
            object value;
            return source != null && source.TryGetValue(name, out value)
                ? value as Dictionary<string, object>
                : null;
        }

        private static string ReadString(Dictionary<string, object> source, string name)
        {
            object value;
            return source != null && source.TryGetValue(name, out value) ? value as string : null;
        }

        private static bool ReadBoolean(Dictionary<string, object> source, string name)
        {
            object value;
            return source != null && source.TryGetValue(name, out value) && value is bool && (bool)value;
        }

        private static string BuildUrl(string dnsName)
        {
            return string.Format("https://{0}:{1}/", TrimDnsName(dnsName), ServePort);
        }

        private static string TrimDnsName(string value)
        {
            return (value ?? string.Empty).Trim().TrimEnd('.');
        }
    }

    internal sealed class TailnetDevice
    {
        public TailnetDevice(string name, string host, string url, string platform, bool current)
        {
            Name = name;
            Host = host;
            Url = url;
            Platform = platform;
            Current = current;
        }

        public string Name { get; private set; }
        public string Host { get; private set; }
        public string Url { get; private set; }
        public string Platform { get; private set; }
        public bool Current { get; private set; }
    }
}
