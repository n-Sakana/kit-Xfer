using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Xfer
{
    /// <summary>The Windows common item dialog (IFileOpenDialog) through raw COM vtables, for Native AOT.
    /// Same contract as src/Common/NativePicker.cs, which the managed Linux build keeps using.</summary>
    internal static unsafe class NativePicker
    {
        private const uint FOS_PICKFOLDERS = 0x20, FOS_FORCEFILESYSTEM = 0x40, FOS_ALLOWMULTISELECT = 0x200, FOS_PATHMUSTEXIST = 0x800, FOS_FILEMUSTEXIST = 0x1000;
        private const int ERROR_CANCELLED = unchecked((int)0x800704C7);
        private const uint SIGDN_FILESYSPATH = 0x80058000;
        private static readonly Guid CLSID_FileOpenDialog = new Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
        private static readonly Guid IID_IFileOpenDialog = new Guid("d57c7288-d4ad-4768-be02-9d969532d960");
        private static readonly Guid IID_IShellItem = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");

        /// <summary>The window that owns the next dialog (set on the UI thread before a picker starts).</summary>
        public static nint Owner;

        public static bool IsAvailable { get { return true; } }

        public static Task<string> PickFolderAsync(string initialPath)
        {
            nint owner = Owner != 0 ? Owner : GetForegroundWindow();
            return RunSta(() => { var picked = Pick(true, initialPath, owner); return picked.Count == 0 ? null : picked[0]; });
        }

        public static Task<List<string>> PickFilesAsync(string initialPath, string mode)
        {
            nint owner = Owner != 0 ? Owner : GetForegroundWindow();
            return RunSta(() => { var picked = Pick(false, initialPath, owner); return picked.Count == 0 ? null : picked; });
        }

        private static Task<T> RunSta<T>(Func<T> operation)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try { completion.SetResult(operation()); }
                catch (Exception error) { completion.SetException(error); }
            }) { IsBackground = true, Name = "Xfer picker" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return completion.Task;
        }

        private static List<string> Pick(bool folder, string initialPath, nint owner)
        {
            var result = new List<string>();
            int init = CoInitializeEx(0, 2 /* COINIT_APARTMENTTHREADED */);
            void* dialog = null, start = null, results = null;
            try
            {
                Guid clsid = CLSID_FileOpenDialog, iid = IID_IFileOpenDialog;
                Check(CoCreateInstance(&clsid, null, 1 /* CLSCTX_INPROC_SERVER */, &iid, &dialog));
                uint options;
                Check(((delegate* unmanaged[Stdcall]<void*, uint*, int>)V(dialog)[10])(dialog, &options)); // GetOptions
                options |= FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST | (folder ? FOS_PICKFOLDERS : FOS_FILEMUSTEXIST | FOS_ALLOWMULTISELECT);
                Check(((delegate* unmanaged[Stdcall]<void*, uint, int>)V(dialog)[9])(dialog, options)); // SetOptions
                fixed (char* title = folder ? "フォルダを選ぶ" : "ファイルを選ぶ")
                    ((delegate* unmanaged[Stdcall]<void*, char*, int>)V(dialog)[17])(dialog, title); // SetTitle
                fixed (char* ok = folder ? "このフォルダを選ぶ" : "選ぶ")
                    ((delegate* unmanaged[Stdcall]<void*, char*, int>)V(dialog)[18])(dialog, ok); // SetOkButtonLabel
                string initial = InitialFolder(initialPath);
                if (initial != null)
                {
                    Guid item = IID_IShellItem;
                    fixed (char* path = initial)
                        if (SHCreateItemFromParsingName(path, null, &item, &start) >= 0 && start != null)
                            ((delegate* unmanaged[Stdcall]<void*, void*, int>)V(dialog)[12])(dialog, start); // SetFolder
                }
                int shown = ((delegate* unmanaged[Stdcall]<void*, nint, int>)V(dialog)[3])(dialog, owner); // Show
                if (shown == ERROR_CANCELLED) return result;
                Check(shown);
                Check(((delegate* unmanaged[Stdcall]<void*, void**, int>)V(dialog)[27])(dialog, &results)); // IFileOpenDialog::GetResults
                uint count;
                Check(((delegate* unmanaged[Stdcall]<void*, uint*, int>)V(results)[7])(results, &count)); // IShellItemArray::GetCount
                for (uint i = 0; i < count; i++)
                {
                    void* item = null;
                    if (((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)V(results)[8])(results, i, &item) < 0 || item == null) continue; // GetItemAt
                    try
                    {
                        char* name = null;
                        if (((delegate* unmanaged[Stdcall]<void*, uint, char**, int>)V(item)[5])(item, SIGDN_FILESYSPATH, &name) >= 0 && name != null) // GetDisplayName
                        {
                            try { result.Add(new string(name)); } finally { CoTaskMemFree(name); }
                        }
                    }
                    finally { Release(item); }
                }
                return result;
            }
            finally
            {
                Release(results); Release(start); Release(dialog);
                if (init >= 0) CoUninitialize();
            }
        }

        private static string InitialFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                if (File.Exists(path)) path = Path.GetDirectoryName(path);
                return Directory.Exists(path) ? Path.GetFullPath(path) : null;
            }
            catch (Exception) { return null; }
        }

        private static void** V(void* p) { return *(void***)p; }
        private static void Release(void* p) { if (p != null) ((delegate* unmanaged[Stdcall]<void*, uint>)V(p)[2])(p); }
        private static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

        [DllImport("ole32.dll", ExactSpelling = true)] private static extern int CoInitializeEx(nint reserved, uint coinit);
        [DllImport("ole32.dll", ExactSpelling = true)] private static extern void CoUninitialize();
        [DllImport("ole32.dll", ExactSpelling = true)] private static extern int CoCreateInstance(Guid* clsid, void* outer, uint context, Guid* iid, void** instance);
        [DllImport("ole32.dll", ExactSpelling = true)] private static extern void CoTaskMemFree(void* memory);
        [DllImport("shell32.dll", ExactSpelling = true)] private static extern int SHCreateItemFromParsingName(char* path, void* bindContext, Guid* iid, void** item);
        [DllImport("user32.dll", ExactSpelling = true)] private static extern nint GetForegroundWindow();
    }
}
