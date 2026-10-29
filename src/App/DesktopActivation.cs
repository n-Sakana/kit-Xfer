using System;

namespace Xfer
{
    internal static class DesktopActivation
    {
        private static readonly object Gate = new object();
        private static Action<string> _handler;

        public static void Register(Action<string> handler)
        {
            if (handler == null)
            {
                throw new ArgumentNullException("handler");
            }

            lock (Gate)
            {
                _handler = handler;
            }
        }

        public static void Unregister(Action<string> handler)
        {
            lock (Gate)
            {
                if (_handler == handler)
                {
                    _handler = null;
                }
            }
        }

        public static bool Request(string mode)
        {
            Action<string> handler;
            lock (Gate)
            {
                handler = _handler;
            }

            if (handler == null)
            {
                return false;
            }

            handler(mode);
            return true;
        }
    }
}
