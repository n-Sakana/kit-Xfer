using System;

namespace Xfer
{
    // D1/0C buffers the whole bundle. D1/0D uses disk and has no application
    // content-size cap; its lengths are signed 64-bit integers.
    internal static class OpticalTransferLimits
    {
        public const int MaximumPayloadBytes = 64 * 1024 * 1024;
        public const int MaximumManifestBytes = 1024 * 1024;
        public const int MaximumContainerBytes = MaximumPayloadBytes + MaximumManifestBytes + (64 * 1024);
        public const int MaximumFiles = 2000;

        // Switch well before the buffered receiver's limit to keep memory bounded
        // on both endpoints, even when the selected content does not compress.
        public static readonly int StreamingThresholdBytes = Math.Min(8 * 1024 * 1024, MaximumPayloadBytes);
        public const int StreamingManifestThresholdBytes = MaximumManifestBytes / 2;
    }
}
