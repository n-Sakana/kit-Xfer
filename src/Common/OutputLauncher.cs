using System;
using System.Diagnostics;
using System.IO;

namespace Xfer
{
    internal static class OutputLauncher
    {
        public static string OpenFolder(string outputPath)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                throw new InvalidOperationException("Create output before opening it.");
            }

            var fullPath = Path.GetFullPath(outputPath);
            string directoryPath;
            if (Directory.Exists(fullPath))
            {
                directoryPath = fullPath;
            }
            else if (File.Exists(fullPath))
            {
                directoryPath = Path.GetDirectoryName(fullPath);
            }
            else
            {
                throw new FileNotFoundException("The output path was not found.", fullPath);
            }

            if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
            {
                throw new DirectoryNotFoundException("The output folder was not found.");
            }

            ProcessStartInfo startInfo;
            if (PlatformInfo.IsWindows)
            {
                startInfo = new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = Quote(directoryPath),
                    UseShellExecute = false
                };
            }
            else if (Environment.OSVersion.Platform == PlatformID.MacOSX)
            {
                startInfo = new ProcessStartInfo
                {
                    FileName = "open",
                    Arguments = Quote(directoryPath),
                    UseShellExecute = false
                };
            }
            else
            {
                startInfo = new ProcessStartInfo
                {
                    FileName = "xdg-open",
                    Arguments = Quote(directoryPath),
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
            }

            Process.Start(startInfo);
            return directoryPath;
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
