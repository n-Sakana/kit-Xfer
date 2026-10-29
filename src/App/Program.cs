using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Xfer
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            return Run(AppDomain.CurrentDomain.BaseDirectory, args);
        }

        public static int Run(string appDirectory, string[] args)
        {
            Diagnostics.Event("app.start", "http", null);
            Console.OutputEncoding = new UTF8Encoding(false);

            AppOptions options;
            try
            {
                options = AppOptions.Parse(args);
            }
            catch (ArgumentException exception)
            {
                Diagnostics.Failure("app.start", "http", exception);
                Console.Error.WriteLine(exception.Message);
                Console.Error.WriteLine("Use --help to see the available options.");
                return 2;
            }

            if (options.ShowHelp)
            {
                AppOptions.WriteHelp(Console.Out);
                return 0;
            }


            int runningProcessId;
            string discoveryError;
            if (TryFindRunningXfer(
                options.Port,
                appDirectory,
                out runningProcessId,
                out discoveryError))
            {
                if (discoveryError != null)
                {
                    Console.Error.WriteLine(
                        "Xfer could not replace the running Xfer: {0}",
                        discoveryError);
                    return 1;
                }

                Console.WriteLine(
                    "Replacing the running Xfer (pid {0}).",
                    runningProcessId);

                string stopError;
                if (!TryStopRunningXfer(
                    options.Port,
                    runningProcessId,
                    out stopError))
                {
                    Console.Error.WriteLine(
                        "Xfer could not stop the running Xfer (pid {0}): {1}",
                        runningProcessId,
                        stopError);
                    return 1;
                }
            }

            AppState state;
            try
            {
                StaticAssets.Initialize(
                    appDirectory,
                    Environment.GetEnvironmentVariable("Xfer_BUILD_ID"));
                state = new AppState(options.InitialPath);
            }
            catch (ArgumentException exception)
            {
                Diagnostics.Failure("app.start", "http", exception);
                Console.Error.WriteLine(exception.Message);
                return 2;
            }
            catch (DirectoryNotFoundException exception)
            {
                Diagnostics.Failure("app.start", "http", exception);
                Console.Error.WriteLine(exception.Message);
                return 2;
            }
            catch (IOException exception)
            {
                Diagnostics.Failure("app.start", "http", exception);
                Console.Error.WriteLine(exception.Message);
                return 2;
            }

            using (var shutdown = new CancellationTokenSource())
            using (var server = new LocalServer(
                options.Port,
                state,
                options.InitialMode,
                Path.Combine(Path.GetFullPath(appDirectory), "output"),
                shutdown.Cancel))
            {
                Uri appUri;
                try
                {
                    appUri = server.Start();
                }
                catch (Exception exception)
                {
                    Diagnostics.Failure("server.start", "http", exception);
                    Console.Error.WriteLine(
                        "Xfer could not listen on localhost:{0}: {1}",
                        options.Port,
                        exception.Message);
                    return 1;
                }

                Console.WriteLine("Xfer is listening at {0}", appUri);
                Console.WriteLine(
                    "Input: {0}",
                    state.ReadFolder(options.InitialMode ?? "optical").Path);
                if (options.Port == AppOptions.DefaultPort)
                {
                    string tailnetUrl;
                    string tailnetError;
                    if (TailnetDiscovery.TryConfigureServe(
                            options.Port,
                            out tailnetUrl,
                            out tailnetError))
                    {
                        Console.WriteLine(
                            "Tailnet: {0}",
                            string.IsNullOrWhiteSpace(tailnetUrl)
                                ? "HTTPS port 10000"
                                : tailnetUrl);
                        if (!string.IsNullOrWhiteSpace(tailnetError))
                        {
                            Console.WriteLine("Tailnet status: {0}", tailnetError);
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(tailnetError))
                    {
                        Console.WriteLine(
                            "Tailnet remote control is unavailable: {0}",
                            tailnetError);
                    }
                }
                Console.WriteLine("Press Ctrl+C to stop.");

                ConsoleCancelEventHandler handler = delegate (object sender, ConsoleCancelEventArgs eventArgs)
                {
                    eventArgs.Cancel = true;
                    shutdown.Cancel();
                };
                Console.CancelKeyPress += handler;

                try
                {
                    Task serverTask = server.RunAsync(shutdown.Token);
                    serverTask.ContinueWith(
                        delegate { shutdown.Cancel(); },
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);

                    if (!options.NoBrowser)
                    {
                        Console.WriteLine(
                            "The Xfer window opens from Xfer.vbs or ./Xfer; the HTTP server will remain running.");
                    }

                    serverTask.GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    // Normal Ctrl+C or replacement shutdown.
                }
                finally
                {
                    Console.CancelKeyPress -= handler;
                }
            }

            return 0;
        }

        internal static bool TryFindRunningXfer(
            int port,
            string appDirectory,
            out int processId,
            out string error)
        {
            processId = 0;
            error = null;
            var statusUri = new Uri(string.Format("http://127.0.0.1:{0}/api/status", port));

            try
            {
#if NET9_0_OR_GREATER
#pragma warning disable SYSLIB0014
#endif
                var request = WebRequest.CreateHttp(statusUri);
#if NET9_0_OR_GREATER
#pragma warning restore SYSLIB0014
#endif
                request.Method = "GET";
                request.Accept = "application/json";
                request.AllowAutoRedirect = false;
                request.Proxy = null;
                request.Timeout = 1000;
                request.ReadWriteTimeout = 1000;

                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        return false;
                    }

                    using (var body = new MemoryStream())
                    using (var stream = response.GetResponseStream())
                    {
                        if (stream == null)
                        {
                            return false;
                        }

                        stream.CopyTo(body);
                        var status = JsonCodec.ParseObject(body.ToArray());
                        if (!IsXferStatus(status))
                        {
                            return false;
                        }

                        if (TryReadProcessId(status, out processId))
                        {
                            return true;
                        }

                        string legacyError;
                        if (TryFindLegacyHostProcess(
                            appDirectory,
                            out processId,
                            out legacyError))
                        {
                            return true;
                        }

                        error = string.Format(
                            "The running Xfer did not report its process ID, and its host process could not be identified: {0}",
                            legacyError);
                        return true;
                    }
                }
            }
            catch (WebException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static bool TryReadProcessId(
            Dictionary<string, object> status,
            out int processId)
        {
            processId = 0;
            object value;
            if (!status.TryGetValue("processId", out value) || !(value is decimal))
            {
                return false;
            }

            var number = (decimal)value;
            if (number < 1 || number > int.MaxValue || decimal.Truncate(number) != number)
            {
                return false;
            }

            processId = (int)number;
            return true;
        }

        private static bool TryFindLegacyHostProcess(
            string appDirectory,
            out int processId,
            out string error)
        {
            processId = 0;
            error = null;
            if (!PlatformInfo.IsWindows)
            {
                error = "legacy process discovery is only available on Windows";
                return false;
            }

            var scriptPath = Path.GetFullPath(Path.Combine(appDirectory, "Xfer.ps1"));
            var matches = new List<int>();
            object searcher = null;
            object results = null;

            try
            {
                var management = Assembly.Load(
                    "System.Management, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a");
                var searcherType = management.GetType(
                    "System.Management.ManagementObjectSearcher",
                    true);
                searcher = Activator.CreateInstance(
                    searcherType,
                    new object[]
                    {
                        "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'powershell.exe'"
                    });
                results = searcherType.GetMethod("Get", Type.EmptyTypes).Invoke(searcher, null);

                var currentProcessId = GetCurrentProcessId();
                foreach (var item in (System.Collections.IEnumerable)results)
                {
                    try
                    {
                        var itemType = item.GetType();
                        var readProperty = itemType.GetMethod(
                            "GetPropertyValue",
                            new[] { typeof(string) });
                        var commandLine = readProperty.Invoke(
                            item,
                            new object[] { "CommandLine" }) as string;
                        var rawProcessId = readProperty.Invoke(
                            item,
                            new object[] { "ProcessId" });
                        if (commandLine == null || rawProcessId == null)
                        {
                            continue;
                        }

                        var candidateProcessId = Convert.ToInt32(rawProcessId);
                        if (candidateProcessId != currentProcessId
                            && commandLine.IndexOf(
                                scriptPath,
                                StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            matches.Add(candidateProcessId);
                        }
                    }
                    finally
                    {
                        DisposeIfNeeded(item);
                    }
                }
            }
            catch (Exception exception)
            {
                error = exception.GetBaseException().Message;
                return false;
            }
            finally
            {
                DisposeIfNeeded(results);
                DisposeIfNeeded(searcher);
            }

            if (matches.Count == 1)
            {
                processId = matches[0];
                return true;
            }

            error = matches.Count == 0
                ? "no matching Windows PowerShell process was found"
                : "more than one matching Windows PowerShell process was found";
            return false;
        }

        internal static bool TryStopRunningXfer(
            int port,
            int processId,
            out string error)
        {
            error = null;
            if (processId == GetCurrentProcessId())
            {
                error = "the reported process ID belongs to this launcher";
                return false;
            }

            try
            {
                using (var process = Process.GetProcessById(processId))
                {
                    if (process.HasExited)
                    {
                        return true;
                    }

                    string shutdownError;
                    if (TryRequestShutdown(port, processId, out shutdownError)
                        && process.WaitForExit(5000))
                    {
                        return true;
                    }

                    process.Kill();
                    if (!process.WaitForExit(5000))
                    {
                        error = string.IsNullOrWhiteSpace(shutdownError)
                            ? "the process did not exit within 5 seconds"
                            : string.Format(
                                "the process did not exit within 5 seconds after the shutdown request failed: {0}",
                                shutdownError);
                        return false;
                    }
                }

                return true;
            }
            catch (ArgumentException)
            {
                // The process exited after reporting its status.
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        private static bool TryRequestShutdown(
            int port,
            int processId,
            out string error)
        {
            error = null;
            var shutdownUri = new Uri(string.Format(
                "http://127.0.0.1:{0}/api/shutdown",
                port));

            try
            {
#if NET9_0_OR_GREATER
#pragma warning disable SYSLIB0014
#endif
                var request = WebRequest.CreateHttp(shutdownUri);
#if NET9_0_OR_GREATER
#pragma warning restore SYSLIB0014
#endif
                request.Method = "POST";
                request.ContentLength = 0;
                request.Headers["X-Xfer-Process-Id"] = processId.ToString();
                request.AllowAutoRedirect = false;
                request.Proxy = null;
                request.Timeout = 1000;
                request.ReadWriteTimeout = 1000;

                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        return true;
                    }

                    error = string.Format(
                        "shutdown request returned HTTP {0}",
                        (int)response.StatusCode);
                    return false;
                }
            }
            catch (WebException exception)
            {
                error = exception.Message;
                return false;
            }
            catch (IOException exception)
            {
                error = exception.Message;
                return false;
            }
        }

        private static int GetCurrentProcessId()
        {
            using (var process = Process.GetCurrentProcess())
            {
                return process.Id;
            }
        }

        private static void DisposeIfNeeded(object value)
        {
            var disposable = value as IDisposable;
            if (disposable != null)
            {
                disposable.Dispose();
            }
        }

        private static bool IsXferStatus(Dictionary<string, object> status)
        {
            object application;
            object device;
            object platform;
            object version;
            object capabilities;
            return status.TryGetValue("application", out application) && object.Equals(application, "Xfer")
                && status.TryGetValue("device", out device) && device is string
                && status.TryGetValue("platform", out platform) && platform is string
                && status.TryGetValue("version", out version) && version is string
                && status.TryGetValue("capabilities", out capabilities)
                && capabilities is Dictionary<string, object>;
        }

    }
}
