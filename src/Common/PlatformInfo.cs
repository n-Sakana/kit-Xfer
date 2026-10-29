using System;

namespace Xfer
{
    internal static class PlatformInfo
    {
        public static bool IsWindows
        {
            get { return Environment.OSVersion.Platform == PlatformID.Win32NT; }
        }

        public static bool IsLinux
        {
            get
            {
                var platform = Environment.OSVersion.Platform;
                return platform == PlatformID.Unix && !IsMac;
            }
        }

        private static bool IsMac
        {
            get
            {
                return Environment.OSVersion.Platform == PlatformID.MacOSX;
            }
        }

        public static string Name
        {
            get
            {
                if (IsWindows)
                {
                    return "Windows";
                }

                if (IsLinux)
                {
                    return "Linux";
                }

                return "Other";
            }
        }
    }
}
