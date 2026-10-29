using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Xfer
{
    internal static class NativePicker
    {
        private const uint FosPickFolders = 0x00000020;
        private const uint FosForceFileSystem = 0x00000040;
        private const uint FosPathMustExist = 0x00000800;
        private const uint FosFileMustExist = 0x00001000;
        private const int ErrorCancelled = unchecked((int)0x800704C7);
        private const uint SigdnFileSystemPath = 0x80058000;

        public static bool IsAvailable
        {
            get
            {
                return PlatformInfo.IsWindows
                    || (PlatformInfo.IsLinux
                        && (FindCommand("zenity") != null || FindCommand("kdialog") != null));
            }
        }

        public static Task<string> PickFolderAsync(string initialPath)
        {
            if (PlatformInfo.IsWindows)
            {
                var owner = GetForegroundWindow();
                return RunSta(
                    delegate { return PickWindowsFolder(initialPath, owner); },
                    "Xfer folder picker");
            }
            if (PlatformInfo.IsLinux)
            {
                return Task.Run(delegate { return PickLinuxFolder(initialPath); });
            }

            throw new PlatformNotSupportedException(
                "OS のフォルダ選択ダイアログはこの環境では使えません。");
        }

        public static Task<List<string>> PickFilesAsync(string initialPath, string mode)
        {
            if (PlatformInfo.IsWindows)
            {
                var owner = GetForegroundWindow();
                return RunSta(
                    delegate { return PickWindowsFiles(initialPath, mode, owner); },
                    "Xfer file picker");
            }
            if (PlatformInfo.IsLinux)
            {
                return Task.Run(delegate { return PickLinuxFiles(initialPath, mode); });
            }

            throw new PlatformNotSupportedException(
                "OS のファイル選択ダイアログはこの環境では使えません。");
        }

        private static Task<T> RunSta<T>(Func<T> operation, string name)
        {
            var completion = new TaskCompletionSource<T>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(new ThreadStart(delegate
            {
                try
                {
                    completion.SetResult(operation());
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
            }));
            thread.IsBackground = true;
            thread.Name = name;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return completion.Task;
        }

        private static string PickWindowsFolder(string initialPath, IntPtr owner)
        {
            IFileDialog dialog = null;
            IShellItem initialFolder = null;
            IShellItem selectedFolder = null;

            try
            {
                dialog = (IFileDialog)new FileOpenDialogInstance();

                uint options;
                dialog.GetOptions(out options);
                dialog.SetOptions(options | FosPickFolders | FosForceFileSystem | FosPathMustExist);
                dialog.SetTitle("フォルダを選ぶ");
                dialog.SetOkButtonLabel("このフォルダを選ぶ");
                SetInitialFolder(dialog, initialPath, out initialFolder);

                if (owner != IntPtr.Zero)
                {
                    SetForegroundWindow(owner);
                }
                var showResult = dialog.Show(owner);
                if (showResult == ErrorCancelled)
                {
                    return null;
                }
                if (showResult != 0)
                {
                    Marshal.ThrowExceptionForHR(showResult);
                }

                dialog.GetResult(out selectedFolder);
                return ShellPath(selectedFolder);
            }
            finally
            {
                ReleaseComObject(selectedFolder);
                ReleaseComObject(initialFolder);
                ReleaseComObject(dialog);
            }
        }

        private static List<string> PickWindowsFiles(
            string initialPath,
            string mode,
            IntPtr owner)
        {
            IFileDialog dialog = null;
            IShellItem initialFolder = null;
            IShellItem selectedItem = null;

            try
            {
                dialog = (IFileDialog)new FileOpenDialogInstance();

                uint options;
                dialog.GetOptions(out options);
                dialog.SetOptions(
                    options | FosForceFileSystem | FosPathMustExist | FosFileMustExist);
                dialog.SetTitle(FilePickerTitle(mode));
                dialog.SetOkButtonLabel("選ぶ");

                var filter = new[]
                {
                    new ComDlgFilterSpec(
                        FilePickerFilterName(mode),
                        FolderCatalog.PickerPattern(mode))
                };
                dialog.SetFileTypes((uint)filter.Length, filter);
                dialog.SetFileTypeIndex(1);
                SetInitialFolder(dialog, initialPath, out initialFolder);

                if (owner != IntPtr.Zero)
                {
                    SetForegroundWindow(owner);
                }
                var showResult = dialog.Show(owner);
                if (showResult == ErrorCancelled)
                {
                    return null;
                }
                if (showResult != 0)
                {
                    Marshal.ThrowExceptionForHR(showResult);
                }

                dialog.GetResult(out selectedItem);
                return new List<string> { ShellPath(selectedItem) };
            }
            finally
            {
                ReleaseComObject(selectedItem);
                ReleaseComObject(initialFolder);
                ReleaseComObject(dialog);
            }
        }

        private static void SetInitialFolder(
            IFileDialog dialog,
            string initialPath,
            out IShellItem initialFolder)
        {
            initialFolder = null;
            var folderPath = initialPath;
            if (!string.IsNullOrWhiteSpace(folderPath) && File.Exists(folderPath))
            {
                folderPath = Path.GetDirectoryName(folderPath);
            }
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                return;
            }

            var shellItemId = typeof(IShellItem).GUID;
            var createResult = SHCreateItemFromParsingName(
                folderPath,
                IntPtr.Zero,
                ref shellItemId,
                out initialFolder);
            if (createResult >= 0 && initialFolder != null)
            {
                dialog.SetFolder(initialFolder);
            }
        }

        private static string PickLinuxFolder(string initialPath)
        {
            var zenity = FindCommand("zenity");
            if (zenity != null)
            {
                var arguments = new List<string>
                {
                    "--file-selection",
                    "--directory",
                    "--title=フォルダを選ぶ"
                };
                AddZenityInitialPath(arguments, initialPath);
                return FirstOutputLine(RunDialog(zenity, arguments));
            }

            var kdialog = FindCommand("kdialog");
            if (kdialog != null)
            {
                return FirstOutputLine(RunDialog(
                    kdialog,
                    new[]
                    {
                        "--getexistingdirectory",
                        InitialDirectory(initialPath),
                        "--title",
                        "フォルダを選ぶ"
                    }));
            }

            throw new PlatformNotSupportedException(
                "zenity または kdialog が見つかりません。");
        }

        private static List<string> PickLinuxFiles(string initialPath, string mode)
        {
            string output;
            var zenity = FindCommand("zenity");
            if (zenity != null)
            {
                var arguments = new List<string>
                {
                    "--file-selection",
                    "--title=" + FilePickerTitle(mode),
                    "--file-filter=" + FilePickerFilterName(mode) + " | " +
                        FolderCatalog.PickerPattern(mode).Replace(';', ' ')
                };
                AddZenityInitialPath(arguments, initialPath);
                output = RunDialog(zenity, arguments);
            }
            else
            {
                var kdialog = FindCommand("kdialog");
                if (kdialog == null)
                {
                    throw new PlatformNotSupportedException(
                        "zenity または kdialog が見つかりません。");
                }

                output = RunDialog(
                    kdialog,
                    new[]
                    {
                        "--getopenfilename",
                        InitialDirectory(initialPath),
                        FilePickerFilterName(mode) + " (" + FolderCatalog.PickerPattern(mode) + ")",
                        "--title",
                        FilePickerTitle(mode)
                    });
            }

            if (output == null)
            {
                return null;
            }

            var normalized = output.Replace("\\n", "\n");
            var lines = normalized.Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries);
            var paths = new List<string>();
            foreach (var line in lines)
            {
                var path = line.Trim();
                if (path.Length > 0)
                {
                    paths.Add(path);
                }
            }
            return paths;
        }

        private static string RunDialog(string command, IEnumerable<string> arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = JoinArguments(arguments),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (var process = Process.Start(startInfo))
            {
                if (process == null)
                {
                    throw new IOException("OS の選択ダイアログを起動できませんでした。");
                }

                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode == 0)
                {
                    return output.TrimEnd('\r', '\n');
                }
                if (process.ExitCode == 1 || process.ExitCode == 255)
                {
                    return null;
                }

                throw new IOException(string.IsNullOrWhiteSpace(error)
                    ? "OS の選択ダイアログが失敗しました。"
                    : error.Trim());
            }
        }

        private static void AddZenityInitialPath(List<string> arguments, string initialPath)
        {
            var folder = InitialDirectory(initialPath);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                arguments.Add("--filename=" + folder.TrimEnd(Path.DirectorySeparatorChar) +
                    Path.DirectorySeparatorChar);
            }
        }

        private static string InitialDirectory(string initialPath)
        {
            if (!string.IsNullOrWhiteSpace(initialPath))
            {
                if (Directory.Exists(initialPath))
                {
                    return Path.GetFullPath(initialPath);
                }
                if (File.Exists(initialPath))
                {
                    return Path.GetDirectoryName(Path.GetFullPath(initialPath));
                }
            }
            return Path.GetFullPath(Directory.GetCurrentDirectory());
        }

        private static string FirstOutputLine(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                return null;
            }
            return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        }

        private static string FilePickerTitle(string mode)
        {
            return "送るファイルを選ぶ";
        }

        private static string FilePickerFilterName(string mode)
        {
            return "すべてのファイル";
        }

        private static string FindCommand(string name)
        {
            var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var directory in pathValue.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }
                try
                {
                    var candidate = Path.Combine(directory.Trim(), name);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (ArgumentException)
                {
                }
            }
            return null;
        }

        private static string JoinArguments(IEnumerable<string> arguments)
        {
            var values = new List<string>();
            foreach (var argument in arguments)
            {
                values.Add(QuoteArgument(argument));
            }
            return string.Join(" ", values.ToArray());
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + (value ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"") + "\"";
        }

        private static string ShellPath(IShellItem item)
        {
            string path;
            item.GetDisplayName(SigdnFileSystemPath, out path);
            return path;
        }

        private static void ReleaseComObject(object value)
        {
            if (value != null && Marshal.IsComObject(value))
            {
                Marshal.ReleaseComObject(value);
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string path,
            IntPtr bindContext,
            ref Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

        [ComImport]
        [Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
        [ClassInterface(ClassInterfaceType.None)]
        private class FileOpenDialogInstance
        {
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ComDlgFilterSpec
        {
            [MarshalAs(UnmanagedType.LPWStr)]
            public string Name;

            [MarshalAs(UnmanagedType.LPWStr)]
            public string Spec;

            public ComDlgFilterSpec(string name, string spec)
            {
                Name = name;
                Spec = spec;
            }
        }

        [ComImport]
        [Guid("42F85136-DB7E-439C-85F1-E4075D135FC8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileDialog
        {
            [PreserveSig]
            int Show(IntPtr parent);

            void SetFileTypes(
                uint count,
                [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] ComDlgFilterSpec[] filterSpec);
            void SetFileTypeIndex(uint fileType);
            void GetFileTypeIndex(out uint fileType);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint options);
            void GetOptions(out uint options);
            void SetDefaultFolder(IShellItem folder);
            void SetFolder(IShellItem folder);
            void GetFolder(out IShellItem folder);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem item);
            void AddPlace(IShellItem item, int alignment);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
            void Close(int result);
            void SetClientGuid(ref Guid clientId);
            void ClearClientData();
            void SetFilter(IntPtr filter);
        }

        [ComImport]
        [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(
                IntPtr bindContext,
                ref Guid handler,
                ref Guid interfaceId,
                out IntPtr result);
            void GetParent(out IShellItem parent);
            void GetDisplayName(
                uint name,
                [MarshalAs(UnmanagedType.LPWStr)] out string value);
            void GetAttributes(uint mask, out uint attributes);
            void Compare(IShellItem other, uint hint, out int order);
        }

    }
}
