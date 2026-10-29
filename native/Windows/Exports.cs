using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Xfer
{
    /// <summary>The C ABI MiniUI's Windows host loads (docs/native-component.md in miniUI): miniui_call for JSON
    /// requests and miniui_image for app: images. Both run on the window's UI thread.</summary>
    public static unsafe class Exports
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, false);

        [UnmanagedCallersOnly(EntryPoint = "xfer_shutdown", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static int Shutdown(int timeoutMilliseconds)
        {
            try { return NativeShutdown.Finish(timeoutMilliseconds); }
            catch (Exception error) { Diagnostics.Failure("native.shutdown_export", "", error); return 2; }
        }

        [UnmanagedCallersOnly(EntryPoint = "miniui_call", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static int Call(byte* input, int inputLength, byte* output, int outputCapacity)
        {
            try
            {
                NativePicker.Owner = GetActiveWindow();
                string reply = NativeApp.Instance.Call(Utf8.GetString(input, inputLength));
                int count = Utf8.GetByteCount(reply);
                if (count > outputCapacity) {
                    Diagnostics.Sample("native.reply_capacity", "", 0, new { Required = count, Capacity = outputCapacity });
                    return Fail("{\"error\":\"結果が大きすぎます。\"}", output, outputCapacity);
                }
                fixed (char* text = reply) return Utf8.GetBytes(text, reply.Length, output, outputCapacity);
            }
            catch (Exception error) { Diagnostics.Failure("native.export_call", "", error); return Fail(error.Message, output, outputCapacity); }
        }

        [UnmanagedCallersOnly(EntryPoint = "miniui_image", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static int Image(byte* name, int nameLength, byte* pixels, int capacity, int* width, int* height)
        {
            string stage = "Exports.Image:lookup";
            try
            {
                var image = NativeApp.Instance.Image(Utf8.GetString(name, nameLength));
                if (image == null) return -1;
                long bytes = (long)image.Width * image.Height * 4;
                if (bytes > int.MaxValue - 2) return -1;
                if (bytes > capacity) return (int)(-bytes - 2);
                stage = "Exports.Image:copy_pixels";
                image.CopyTo(new Span<byte>(pixels, (int)bytes));
                *width = image.Width; *height = image.Height;
                return (int)bytes;
            }
            catch (Exception error) { Diagnostics.Sample("native.image_failed", "", 0, new { Type = error.GetType().Name, Code = error.HResult, Site = stage }); return -1; }
        }

        private static int Fail(string message, byte* output, int capacity)
        {
            var bytes = Utf8.GetBytes(message);
            int count = Math.Min(bytes.Length, capacity);
            Marshal.Copy(bytes, 0, (nint)output, count);
            return -count - 1;
        }

        [DllImport("user32.dll", ExactSpelling = true)] private static extern nint GetActiveWindow();
    }
}
