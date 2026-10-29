using System;

namespace Xfer
{
    /// <summary>How the camera is set for reading a screen. A screen switches its QR between two pictures, so a short
    /// exposure keeps that switch out of most pictures; a fixed focus keeps the autofocus from hunting on a pattern
    /// that changes thirty times a second. Null leaves the camera's current setting unchanged.</summary>
    internal sealed class CameraTuning
    {
        /// <summary>Manual exposure as log2 seconds (-7 = 1/128 s); null leaves it unchanged.</summary>
        public int? ExposureLog2 = null;
        /// <summary>Manual focus in the camera's units; null leaves it unchanged.</summary>
        public int? Focus = null;
        /// <summary>Manual gain in the camera's units; null leaves it unchanged.</summary>
        public int? Gain = null;

        public override string ToString()
        {
            return "exposure=" + (ExposureLog2.HasValue ? ExposureLog2.Value.ToString() : "keep")
                + " focus=" + (Focus.HasValue ? Focus.Value.ToString() : "keep")
                + " gain=" + (Gain.HasValue ? Gain.Value.ToString() : "keep");
        }
    }

    /// <summary>One camera control as the device reports it.</summary>
    internal struct CameraControlState
    {
        public bool Supported;
        public int Minimum, Maximum, Step, Default, Value;
        public bool Automatic;
        public int Capabilities, Flags;
        public override string ToString()
        {
            return !Supported ? "unsupported" : Value + (Automatic ? " (auto)" : " (manual)") + " range " + Minimum + ".." + Maximum + " step " + Step + " default " + Default;
        }
    }
}
