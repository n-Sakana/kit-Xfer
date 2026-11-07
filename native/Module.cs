// ABI v2: synchronous JSON and BGRA images, owned by the main document.
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Xfer
{
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ModuleBytes { public byte* Data; public nuint Length; }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ModuleHost
    {
        public uint Size;
        public void* Context;
        public nint Push, Reply, Present, Log;
        public delegate* unmanaged[Cdecl]<void*, void*, ModuleBytes, int> Append;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ModuleInfo
    {
        public uint Size;
        public byte* Root, State, AppId;
        public uint PreferredKind;
    }

    public static unsafe class Module
    {
        private static bool _loaded;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        [UnmanagedCallersOnly(EntryPoint = "miniui_module_abi_version", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static uint Version() => 2;

        [UnmanagedCallersOnly(EntryPoint = "miniui_module_init_v2", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static int Init(ModuleHost* host, ModuleInfo* info, void** instance, byte* error, nuint capacity)
        {
            *instance = null;
            try
            {
                if (host == null || host->Size < sizeof(ModuleHost) || host->Append == null ||
                    info == null || info->Size < sizeof(ModuleInfo) || info->PreferredKind != 0)
                    throw new ArgumentException("Invalid ABI v2 host or module info.");
                if (_loaded) throw new InvalidOperationException("Xfer's app belongs to its main document.");
                var copy = (ModuleHost*)NativeMemory.Alloc((nuint)sizeof(ModuleHost));
                *copy = *host;
                *instance = copy;
                _loaded = true;
                return 0;
            }
            catch (Exception e)
            {
                var bytes = Utf8.GetBytes(e.Message);
                if (capacity > 0) {
                    int n = (int)Math.Min((nuint)bytes.Length, capacity - 1);
                    bytes.AsSpan(0, n).CopyTo(new Span<byte>(error, n));
                    error[n] = 0;
                }
                return -1;
            }
        }

        [UnmanagedCallersOnly(EntryPoint = "miniui_module_call_v2", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static int Call(void* instance, ModuleBytes input, uint kind, void* output, uint* outputKind)
        {
            try
            {
                if (kind != 0 || input.Length > 65536) return -1;
#if WINDOWS
                NativePicker.Owner = GetActiveWindow();
#endif
                var bytes = Utf8.GetBytes(NativeApp.Instance.Call(Utf8.GetString(new ReadOnlySpan<byte>(input.Data, (int)input.Length))));
                *outputKind = 0;
                var host = (ModuleHost*)instance;
                fixed (byte* data = bytes)
                    return host->Append(host->Context, output, new ModuleBytes { Data = data, Length = (nuint)bytes.Length }) == 1 ? 0 : -1;
            }
            catch (Exception error) { Diagnostics.Failure("native.export_call", "", error); return -1; }
        }

        [UnmanagedCallersOnly(EntryPoint = "miniui_module_image_v2", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static int Image(void* instance, byte* name, void* output, int* width, int* height)
        {
            try
            {
                var image = NativeApp.Instance.Image(Marshal.PtrToStringUTF8((nint)name));
                if (image == null) return -1;
                var bytes = new byte[image.Width * image.Height * 4];
                image.CopyTo(bytes);
                *width = image.Width; *height = image.Height;
                var host = (ModuleHost*)instance;
                fixed (byte* data = bytes)
                    return host->Append(host->Context, output, new ModuleBytes { Data = data, Length = (nuint)bytes.Length }) == 1 ? 0 : -1;
            }
            catch (Exception error) { Diagnostics.Failure("native.image_failed", "", error); return -1; }
        }

        // Xfer's existing jobs are polled through Call; no asynchronous ABI requests.
        [UnmanagedCallersOnly(EntryPoint = "miniui_module_request_v2", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static int Request(void* instance, ulong id, ModuleBytes input, uint kind) => -1;

        [UnmanagedCallersOnly(EntryPoint = "miniui_module_cancel_v2", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static int Cancel(void* instance, ulong id) => 0;

        [UnmanagedCallersOnly(EntryPoint = "xfer_run_cli", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static int RunCli(int count, byte** values, byte* home)
        {
            try {
                var args = new string[count];
                for (int i = 0; i < count; i++) args[i] = Marshal.PtrToStringUTF8((nint)values[i]);
                return Program.Run(Marshal.PtrToStringUTF8((nint)home), args);
            }
            catch (Exception error) { Console.Error.WriteLine("Xfer: " + error.Message); return 1; }
        }

        [UnmanagedCallersOnly(EntryPoint = "miniui_module_shutdown_v2", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static void Shutdown(void* instance)
        {
            NativeShutdown.Finish(2000);
            NativeMemory.Free(instance);
        }

#if WINDOWS
        [DllImport("user32.dll", ExactSpelling = true)] private static extern nint GetActiveWindow();
#endif
    }
}
