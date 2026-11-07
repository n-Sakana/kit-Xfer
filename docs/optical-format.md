# Optical transfer formats

The desktop sender supports Tile (`fx2`) and QR display layouts. The desktop
receiver detects either layout; the phone web app supports QR only.

## Tile

Tile fills the display with cells within the host's image limits. Larger displays
use a centred viewport at the same device pixels per cell (see
[Linux image limits](../tools/linux/README.md)). Position markers occur every 48 cells. Each
marker has a black 3×3 center, a white border, and an encoded ID ring. Four
markers enclose a patch containing one fountain frame, a CRC-32 checksum, and
Reed-Solomon parity. Patches decode independently, including in a partial or
perspective view. Patches captured during a display transition can be rejected
and recovered from later frames.

The optional coarse center contains up to 2×2 patches with cells enlarged by
2× or 3×. The optional color layer adds two bits per 2×2 cells, with separate
48-byte Reed-Solomon parity and CRC checks. The receiver also checks the
combined layers. Color is off by default.

Encoding and layout are defined in `src/Optical/Fx2.cs`; marker detection and
sampling are in `src/Optical/Fx2Reader.cs`.

## Bundles

Selections up to 8 MiB, 2,000 files, and a 512 KiB estimated manifest use the
DCF2/DCB1 compressed bundle and fountain path. Exceeding any threshold selects
the streaming format for both QR and Tile. These thresholds select a format;
they do not impose a transfer size limit. Both endpoints must support the
selected format.

The buffered receiver retains a 64 MiB content limit, a 1 MiB manifest limit,
and a 65 MiB + 64 KiB container limit because it assembles and decompresses
the whole bundle in memory. `OpticalTransferLimits` defines these limits for
both desktop endpoints. The sender chooses streaming before reaching them
and checks the final buffered container before starting a display. A file of
267,512,174 bytes therefore uses the existing streaming format; the buffered
64 MiB limit does not apply to it. Streaming has no application content-size
cap beyond the length representation and available storage.
The sender's `send.ready` diagnostic event records `streaming` explicitly so
the selected receive path can be identified without guessing from file size.

Streaming preparation hashes each source file with SHA-256. Display reads the
source files in 64 KiB segments, caching at most 129 segments (128 for the main pass
and one for background repair) without creating a full in-memory bundle or
a temporary copy. Changes to a file's size or modification time during sending
are rejected. Streaming content is not compressed.

The receiver keeps at most 256 active fountain segment decoders in memory:
the current group of at most 128 segments and late camera readings of the preceding group.
It reduces binary equations, so independent repair frames can reconstruct a gap
even when no single-block equation is available to start sparse peeling.
It stores completed segments, known blocks, and completion flags on disk.
Eviction discards unresolved equations and preserves solved blocks for later
passes. Once all segments are present, the receiver extracts files and checks
each SHA-256 hash before renaming the output directory into place. Failed,
stopped, reset, expired, and normally closed sessions remove their temporary
data. Extraction can require up to twice the transfer size in free disk space.
Reception does not resume across application restarts.

Content buffers do not grow with the total transfer size. File lists and sender
metadata still use memory proportional to the number of files. Native lengths
use signed 64-bit integers; browser values are exact through 2^53−1. Operating
system limits and available disk space also apply.

## Streaming frame: D1/0D

Integers are little-endian. The 48-byte header is followed by a fixed-length
block. QR frames are limited to 2,953 bytes. Tile uses the patch capacity for
the selected error correction level.

| Offset | Bytes | Field |
| ---: | ---: | --- |
| 0 | 2 | Magic: `D1 0D` |
| 2 | 2 | First 16 bits of the transfer ID, used as the fountain seed |
| 4 | 4 | Frame number within the segment |
| 8 | 8 | Zero-based segment number |
| 16 | 8 | Total bundle length |
| 24 | 16 | Transfer ID as GUID bytes |
| 40 | 4 | FNV-1a checksum of the segment |
| 44 | 2 | Block length |
| 46 | 2 | Reserved; zero |

Segments contain 65,536 bytes except for the final segment. If a full segment
has B blocks, the sender allocates 3B main-pass frames to it. Groups of up
to 128 adjacent segments are interleaved, spreading a missed camera picture
across the group rather than losing a large part of one segment. Group sizes
differ by at most one segment so a short tail does not lose this time diversity. Each segment's
position rotates between stripes so fixed unreadable screen locations do not
always erase the same segment. Initial frames carry systematic blocks;
subsequent frames carry fountain repair blocks. For
a repair frame, subtract the segment's block count from its frame number to
obtain the fountain sequence. The sender selects existing fountain sequence
numbers whose equations cover between one third and two thirds of the segment's
blocks (rounded down, with at least one block). This avoids leaving isolated
blocks untouched by repair frames. The header, seed, coefficient calculation,
QR identifiers and Tile `fx2` identifier are unchanged. Receivers that only use
sparse peeling can interpret these frames but may need additional passes;
update both endpoints for the reduced-equation receiver.
One out of every ten displayed frames instead supplies a new dense repair equation
for an earlier segment, cycling through the prefix already passed by the main
schedule. Before that prefix exists it repairs the current group; after the first
wrap it covers the whole bundle. This fills small checkpointed gaps while new data
continues, rather than waiting for that segment's next whole-file pass. The main
pass uses the remaining nine frames, so its displayed duration increases by about
one ninth. Large gaps can still require later main passes if too few equations arrive.
After the last segment, the sender repeats the
bundle using different repair frames. A receiver can join mid-transfer or
recover missing blocks on later passes.

The FXB3 bundle contains `FXB3`, a label encoded as a BinaryWriter UTF-8 string,
and a signed 64-bit file count. Each file follows as a relative path in the
same string format, a signed 64-bit length, a 32-byte SHA-256 hash, and content.

The HTTP packed QR response uses the 12-byte FQR1 header. Its sequence field
contains the low 32 bits of the request sequence; JavaScript retains the full
request sequence for matching. Optical segment numbers remain 64-bit.

See [tests](../tests/README.md) for synthetic round trips and file validation.
