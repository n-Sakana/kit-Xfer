using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Xfer
{
    internal sealed partial class NativeApp
    {
        /// <summary>V4L2 cameras: the capture node (index 0) of each device. Xfer_CAMERA_DEVICE=/dev/videoN names
        /// one explicitly.</summary>
        private static List<CameraInfo> Cameras()
        {
            var cameras = new List<CameraInfo>();
            string chosen = Environment.GetEnvironmentVariable("Xfer_CAMERA_DEVICE");
            if (!string.IsNullOrWhiteSpace(chosen))
            {
                cameras.Add(new CameraInfo(chosen.Trim(), FfmpegCamera.DeviceName(chosen.Trim())));
                return cameras;
            }
            const string root = "/sys/class/video4linux";
            if (!Directory.Exists(root)) return cameras;
            var nodes = new List<string>();
            foreach (string dir in Directory.GetDirectories(root, "video*")) nodes.Add(Path.GetFileName(dir));
            nodes.Sort((a, b) => Number(a).CompareTo(Number(b)));
            foreach (string node in nodes)
            {
                string index = FfmpegCamera.ReadLine(Path.Combine(root, node, "index"));
                if (index != null && index != "0") continue; // metadata nodes of the same camera
                string device = "/dev/" + node;
                if (!File.Exists(device)) continue;
                cameras.Add(new CameraInfo(device, FfmpegCamera.DeviceName(device)));
            }
            return cameras;
        }

        private static int Number(string node)
        {
            int n;
            return int.TryParse(node.Substring(5), NumberStyles.None, CultureInfo.InvariantCulture, out n) ? n : int.MaxValue;
        }

        private static ICameraSource OpenCamera(string id, int width, int fps)
        {
            if (string.IsNullOrEmpty(id))
            {
                var cameras = Cameras();
                if (cameras.Count == 0) throw new InvalidOperationException("カメラが見つかりません。");
                id = cameras[0].Id;
            }
            return new FfmpegCamera(id, width, fps);
        }
    }
}
