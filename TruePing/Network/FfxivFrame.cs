using System.Runtime.CompilerServices;

namespace TruePing.Network;

/// <summary>
/// Constants and helpers for the FFXIV low-level frame protocol (FFXIVARR), the layer that
/// wraps every game packet. We only ever read the <b>keepalive</b> segments out of it
/// (client -> server type 7, server -> client type 8); everything else (IPC, compression,
/// encryption) is intentionally ignored.
///
/// This is the only patch-sensitive part of the plugin. The layout below has been stable for
/// years, but if Square Enix ever reshapes the frame header these offsets are where to look.
/// See .dev/UPDATING.md.
/// </summary>
internal static class FfxivFrame
{
    /// <summary>Size of FFXIVARR_PACKET_HEADER. The first segment starts right after it.</summary>
    public const int PacketHeaderSize = 0x28;

    /// <summary>Size of FFXIVARR_PACKET_SEGMENT_HEADER (precedes each segment payload).</summary>
    public const int SegmentHeaderSize = 0x10;

    // --- Packet header field offsets ---
    public const int OffPacketSize = 0x18;        // u32: total bytes of this frame, header included
    public const int OffSegmentCount = 0x1E;      // u16: number of segments in this frame
    public const int OffCompression = 0x21;       // u8:  0 = none, 1 = zlib, 2 = oodle
    public const int OffDecompressedLen = 0x24;   // u32: size of the segments once decompressed

    /// <summary>
    /// Decompressed length of a frame carrying exactly one keepalive segment: a 0x10 segment
    /// header plus the 8-byte keepalive payload (id + timestamp). FFXIV compresses the wire
    /// frame (Oodle), so we cannot read the segment type/id, but a single-segment frame whose
    /// decompressed length is exactly this is, in practice, a keepalive. This is the heuristic
    /// the plugin relies on; see .dev/ARCHITECTURE.md.
    /// </summary>
    public const uint KeepAliveDecompressedLen = SegmentHeaderSize + 0x08; // 0x18 = 24

    // --- Segment header field offsets (relative to the segment start) ---
    public const int OffSegmentSize = 0x00;       // u32: bytes of this segment, header included
    public const int OffSegmentType = 0x0C;       // u16: segment type (see below)

    // --- Keepalive payload (relative to the segment start, i.e. after the 0x10 header) ---
    public const int OffKeepAliveId = SegmentHeaderSize + 0x00;   // u32: id echoed back in the response

    /// <summary>Minimum segment size that still carries a keepalive id (header + id + timestamp).</summary>
    public const int MinKeepAliveSegment = SegmentHeaderSize + 0x08;

    // --- Segment types we care about ---
    public const ushort SegmentKeepAlive = 7;          // client -> server
    public const ushort SegmentKeepAliveResponse = 8;  // server -> client

    /// <summary>Sanity ceilings so a misread length can never make us walk off into garbage.</summary>
    public const uint MaxFrameSize = 1 << 20;     // 1 MiB; real frames are far smaller
    public const ushort MaxSegmentCount = 1000;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe ushort ReadU16(byte* p, int o) => (ushort)(p[o] | (p[o + 1] << 8));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe uint ReadU32(byte* p, int o) =>
        (uint)(p[o] | (p[o + 1] << 8) | (p[o + 2] << 16) | (p[o + 3] << 24));
}
