using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;

namespace Xfer
{
    internal static class ShutdownTests
    {
        public static int Main(string[] args)
        {
            string home = Path.GetFullPath(args[0]), work = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(work);
            string logs = Path.Combine(work, "logs");
            Environment.SetEnvironmentVariable("Xfer_LOG_DIR", logs);
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            string reply = NativeApp.Instance.Call(Encoding.UTF8.GetString(JsonCodec.Serialize(new {
                Op = "init", Args = new[] { "--home", home, "--path", work, "--output", Path.Combine(work, "output"), "--port", port.ToString() }
            })));
            if (reply.Contains("\"error\"")) throw new Exception("Native startup failed");
            Diagnostics.Flush();
            // Hold the actual writer gate, modelling a background write already in progress.
            var writer = typeof(Diagnostics).GetField("_writing", BindingFlags.Static | BindingFlags.NonPublic);
            writer.SetValue(null, 1);
            try {
                Diagnostics.Event("test.before_close", "", new { Value = 73 });
                Diagnostics.Sample("test.last_sample", "", 4, null);
                var clock = Stopwatch.StartNew();
                int code = NativeShutdown.Finish(80);
                if (code != 1 || clock.ElapsedMilliseconds > 1000) throw new Exception("Shutdown wait was not bounded");
            } finally { writer.SetValue(null, 0); }
            if (NativeShutdown.Finish(2000) != 0) throw new Exception("Final flush did not complete");
            string text = string.Join("\n", Directory.GetFiles(logs, "trace-*.jsonl").Select(File.ReadAllText));
            foreach (string name in new[] { "test.before_close", "test.last_sample", "native.shutdown.start", "app.stop", "server.stop", "native.shutdown.end" })
                if (!text.Contains("\"event\":\"" + name + "\"")) throw new Exception("Missing final event: " + name);
            if (text.Split(new[] { "\"event\":\"app.stop\"" }, StringSplitOptions.None).Length != 2)
                throw new Exception("Shutdown ran more than once");
            using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)) {
                try { socket.Connect(IPAddress.Loopback, port); throw new Exception("Owned server is still listening"); }
                catch (SocketException) { }
            }
            Console.WriteLine("PASS native shutdown drains last event and aggregate after an occupied writer gate, stops owned server, runs once and bounds caller wait");
            Console.WriteLine("Managed Linux check only; Windows Native AOT/PowerShell boundary requires its reserved verification slot.");
            return 0;
        }
    }
}
