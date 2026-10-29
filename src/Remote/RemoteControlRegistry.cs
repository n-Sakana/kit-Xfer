using System;
using System.Collections.Generic;

namespace Xfer
{
    internal sealed class RemoteControlRegistry
    {
        private static readonly TimeSpan ActiveWindow = TimeSpan.FromSeconds(12);
        private readonly object _gate = new object();
        private readonly Dictionary<string, RemoteControlState> _clients =
            new Dictionary<string, RemoteControlState>(StringComparer.Ordinal);

        public RemoteHeartbeatResult Heartbeat(string id, string name)
        {
            id = RequireId(id);
            name = CleanName(name);
            lock (_gate)
            {
                Prune(DateTime.UtcNow);
                RemoteControlState client;
                if (!_clients.TryGetValue(id, out client))
                {
                    client = new RemoteControlState(id);
                    _clients.Add(id, client);
                }

                client.Name = name;
                client.LastSeenUtc = DateTime.UtcNow;
                var command = client.PendingCommand;
                client.PendingCommand = null;
                return new RemoteHeartbeatResult(command);
            }
        }

        public void Disconnect(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }
            lock (_gate)
            {
                _clients.Remove(id.Trim());
            }
        }

        public List<RemoteControlInfo> ReadActive()
        {
            lock (_gate)
            {
                var now = DateTime.UtcNow;
                Prune(now);
                var result = new List<RemoteControlInfo>();
                foreach (var client in _clients.Values)
                {
                    result.Add(new RemoteControlInfo(
                        client.Id,
                        client.Name,
                        new DateTimeOffset(client.LastSeenUtc, TimeSpan.Zero)));
                }
                result.Sort(delegate (RemoteControlInfo left, RemoteControlInfo right)
                {
                    return StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
                });
                return result;
            }
        }

        public int SendCommand(
            string action,
            List<string> files,
            string format,
            int frameBytes,
            int framesPerSecond,
            string errorCorrection,
            int displaySize)
        {
            if (action != "showQr" && action != "readCamera")
            {
                throw new ArgumentException("リモコンへの操作が正しくありません。", "action");
            }

            lock (_gate)
            {
                Prune(DateTime.UtcNow);
                foreach (var client in _clients.Values)
                {
                    client.PendingCommand = new RemoteCommand(
                        action,
                        files,
                        format,
                        frameBytes,
                        framesPerSecond,
                        errorCorrection,
                        displaySize);
                }
                return _clients.Count;
            }
        }

        private void Prune(DateTime now)
        {
            var expired = new List<string>();
            foreach (var entry in _clients)
            {
                if (now - entry.Value.LastSeenUtc > ActiveWindow)
                {
                    expired.Add(entry.Key);
                }
            }
            foreach (var id in expired)
            {
                _clients.Remove(id);
            }
        }

        private static string RequireId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("リモコンの識別子がありません。", "id");
            }
            value = value.Trim();
            if (value.Length > 100)
            {
                throw new ArgumentException("リモコンの識別子が長すぎます。", "id");
            }
            return value;
        }

        private static string CleanName(string value)
        {
            value = string.IsNullOrWhiteSpace(value) ? "リモコン" : value.Trim();
            if (value.Length > 40)
            {
                value = value.Substring(0, 40);
            }
            return value;
        }

        private sealed class RemoteControlState
        {
            public RemoteControlState(string id)
            {
                Id = id;
                Name = "リモコン";
                LastSeenUtc = DateTime.UtcNow;
            }

            public string Id { get; private set; }
            public string Name { get; set; }
            public DateTime LastSeenUtc { get; set; }
            public RemoteCommand PendingCommand { get; set; }
        }
    }

    internal sealed class RemoteHeartbeatResult
    {
        public RemoteHeartbeatResult(RemoteCommand command)
        {
            Command = command;
        }

        public RemoteCommand Command { get; private set; }
    }

    internal sealed class RemoteCommand
    {
        public RemoteCommand(
            string action,
            List<string> files,
            string format,
            int frameBytes,
            int framesPerSecond,
            string errorCorrection,
            int displaySize)
        {
            Action = action;
            Files = files;
            Format = format;
            FrameBytes = frameBytes;
            FramesPerSecond = framesPerSecond;
            ErrorCorrection = errorCorrection;
            DisplaySize = displaySize;
        }

        public string Action { get; private set; }
        public List<string> Files { get; private set; }
        public string Format { get; private set; }
        public int FrameBytes { get; private set; }
        public int FramesPerSecond { get; private set; }
        public string ErrorCorrection { get; private set; }
        public int DisplaySize { get; private set; }
    }

    internal sealed class RemoteControlInfo
    {
        public RemoteControlInfo(string id, string name, DateTimeOffset lastSeenUtc)
        {
            Id = id;
            Name = name;
            LastSeenUtc = lastSeenUtc;
        }

        public string Id { get; private set; }
        public string Name { get; private set; }
        public DateTimeOffset LastSeenUtc { get; private set; }
    }
}
