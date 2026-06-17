using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Dalamud.Hooking;

namespace TruePing.Network;

/// <summary>
/// Measures the <b>true</b> application-layer ping by timing the FFXIV keepalive handshake.
///
/// The client periodically sends a tiny keepalive frame; the server answers with one. The
/// round-trip of that exchange, timed with a monotonic clock, is the real latency the player
/// experiences, including server processing and any proxy/VPN (e.g. ExitLag) in the path,
/// which is why it is immune to the "the game socket is on localhost" problem that defeats
/// OS-level RTT readers.
///
/// We observe the handshake by hooking the Winsock send/recv exports (stable OS functions, no
/// game signatures). FFXIV compresses every frame on the wire (Oodle, stateful), so we cannot
/// read the segment type or id at this layer. Instead we identify keepalive frames by their
/// unmistakable <b>shape</b>: a single segment whose decompressed length is exactly 0x18
/// (one segment header plus the 8-byte keepalive payload), and pair an outgoing one with the
/// next incoming one in time order (the keepalive is strictly request/response, one at a time).
/// If a patch ever ships an uncompressed keepalive we also read it precisely (type 7/8 + id).
/// See .dev/ARCHITECTURE.md. The detours run on the game's network thread, so they do the
/// minimum possible work and never throw.
/// </summary>
internal sealed unsafe class KeepAlivePingSource : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int RecvDelegate(nuint socket, byte* buf, int len, int flags);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SendDelegate(nuint socket, byte* buf, int len, int flags);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int WsaRecvDelegate(
        nuint socket, WsaBuf* buffers, uint bufferCount, uint* bytesRecvd, uint* flags,
        nint overlapped, nint completionRoutine);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int WsaSendDelegate(
        nuint socket, WsaBuf* buffers, uint bufferCount, uint* bytesSent, uint flags,
        nint overlapped, nint completionRoutine);

    /// <summary>Win32 WSABUF: { ULONG len; CHAR* buf; }, 16 bytes on x64 (pointer at +8).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct WsaBuf
    {
        public uint Len;
        public byte* Buf;
    }

    private const double TimeoutSeconds = 6.0;   // a keepalive unanswered this long counts as loss
    private const int MaxPending = 256;           // cap on outstanding id-based probes we track
    private const uint MaxBufferCount = 64;       // sanity cap on a WSABUF array length

    private readonly PingMonitor monitor;
    private readonly Hook<RecvDelegate>? recvHook;
    private readonly Hook<SendDelegate>? sendHook;
    private readonly Hook<WsaRecvDelegate>? wsaRecvHook;
    private readonly Hook<WsaSendDelegate>? wsaSendHook;
    private readonly nint ws2Handle;

    // Pairing state. Guarded by pendingGate (touched by the detours on the network thread and by
    // the timeout sweep on the main thread).
    private readonly object pendingGate = new();
    private readonly Dictionary<uint, long> pending = new();          // precise path (uncompressed): id -> sent ticks
    private readonly Dictionary<nuint, long> heuristicPending = new(); // heuristic path: socket handle -> sent ticks of the unanswered keepalive

    public bool Installed { get; }

    /// <summary>Total keepalive frames observed (any direction). 0 after a minute = wrong shape/hook.</summary>
    public long KeepAlivesSeen { get; private set; }

    public KeepAlivePingSource(PingMonitor monitor)
    {
        this.monitor = monitor;

        try
        {
            ws2Handle = NativeLibrary.Load("ws2_32.dll");

            recvHook = TryHook<RecvDelegate>("recv", RecvDetour);
            sendHook = TryHook<SendDelegate>("send", SendDetour);
            wsaRecvHook = TryHook<WsaRecvDelegate>("WSARecv", WsaRecvDetour);
            wsaSendHook = TryHook<WsaSendDelegate>("WSASend", WsaSendDetour);

            bool sendSide = (sendHook?.IsEnabled ?? false) || (wsaSendHook?.IsEnabled ?? false);
            bool recvSide = (recvHook?.IsEnabled ?? false) || (wsaRecvHook?.IsEnabled ?? false);
            Installed = sendSide && recvSide;

            Plugin.Log.Information(
                $"TruePing: keepalive source installed={Installed} " +
                $"(recv={recvHook?.IsEnabled}, send={sendHook?.IsEnabled}, " +
                $"WSARecv={wsaRecvHook?.IsEnabled}, WSASend={wsaSendHook?.IsEnabled}).");
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "TruePing: failed to install the keepalive hooks.");
        }
    }

    private Hook<T>? TryHook<T>(string export, T detour) where T : Delegate
    {
        try
        {
            var addr = NativeLibrary.GetExport(ws2Handle, export);
            var hook = Plugin.GameInterop.HookFromAddress(addr, detour);
            hook.Enable();
            return hook;
        }
        catch (Exception e)
        {
            Plugin.Log.Warning(e, $"TruePing: could not hook {export}.");
            return null;
        }
    }

    /// <summary>
    /// Called once per frame from the main thread: expire a keepalive that never got a response
    /// (counts as packet loss) and log light diagnostics.
    /// </summary>
    public void Update()
    {
        long now = Stopwatch.GetTimestamp();
        long timeoutTicks = (long)(TimeoutSeconds * Stopwatch.Frequency);
        int lost = 0;
        lock (pendingGate)
        {
            if (heuristicPending.Count > 0)
            {
                List<nuint>? expiredSockets = null;
                foreach (var (sock, at) in heuristicPending)
                {
                    if (now - at > timeoutTicks)
                        (expiredSockets ??= new List<nuint>()).Add(sock);
                }
                if (expiredSockets != null)
                {
                    foreach (var sock in expiredSockets) heuristicPending.Remove(sock);
                    lost += expiredSockets.Count;
                }
            }
            if (pending.Count > 0)
            {
                List<uint>? expired = null;
                foreach (var (id, at) in pending)
                {
                    if (now - at > timeoutTicks)
                        (expired ??= new List<uint>()).Add(id);
                }
                if (expired != null)
                {
                    foreach (var id in expired) pending.Remove(id);
                    lost += expired.Count;
                }
            }
        }
        if (lost > 0)
            monitor.AddLoss(lost);
    }

    // --- classic synchronous sockets ---

    private int RecvDetour(nuint socket, byte* buf, int len, int flags)
    {
        int read = recvHook!.Original(socket, buf, len, flags);
        if (read > 0)
        {
            try { Scan(socket, buf, read, outgoing: false); }
            catch { /* never let our parsing break the game's network */ }
        }
        return read;
    }

    private int SendDetour(nuint socket, byte* buf, int len, int flags)
    {
        if (len > 0)
        {
            try { Scan(socket, buf, len, outgoing: true); }
            catch { /* never let our parsing break the game's network */ }
        }
        return sendHook!.Original(socket, buf, len, flags);
    }

    // --- overlapped sockets (what FFXIV actually uses) ---

    private int WsaSendDetour(nuint socket, WsaBuf* buffers, uint bufferCount, uint* bytesSent,
        uint flags, nint overlapped, nint completionRoutine)
    {
        // The data to send is already in the buffers before the call, for both sync and async.
        if (buffers != null && bufferCount > 0 && bufferCount <= MaxBufferCount)
        {
            try
            {
                for (uint i = 0; i < bufferCount; i++)
                {
                    var b = buffers[i];
                    if (b.Buf != null && b.Len > 0)
                        Scan(socket, b.Buf, (int)b.Len, outgoing: true);
                }
            }
            catch { /* never break the game's network */ }
        }
        return wsaSendHook!.Original(socket, buffers, bufferCount, bytesSent, flags, overlapped, completionRoutine);
    }

    private int WsaRecvDetour(nuint socket, WsaBuf* buffers, uint bufferCount, uint* bytesRecvd,
        uint* flags, nint overlapped, nint completionRoutine)
    {
        int ret = wsaRecvHook!.Original(socket, buffers, bufferCount, bytesRecvd, flags, overlapped, completionRoutine);

        // We can only read the data here for a *synchronous* completion (no overlapped structure):
        // then the bytes are in the buffers and the count is filled before returning. For overlapped
        // I/O the data arrives later via the completion port, which we do not observe here. The
        // classic recv hook covers the incoming side on this client.
        if (ret == 0 && overlapped == nint.Zero && bytesRecvd != null
            && buffers != null && bufferCount > 0 && bufferCount <= MaxBufferCount)
        {
            try
            {
                long remaining = *bytesRecvd;
                for (uint i = 0; i < bufferCount && remaining > 0; i++)
                {
                    var b = buffers[i];
                    if (b.Buf == null) break;
                    int take = (int)Math.Min(remaining, b.Len);
                    if (take <= 0) break;
                    Scan(socket, b.Buf, take, outgoing: false);
                    remaining -= take;
                }
            }
            catch { /* never break the game's network */ }
        }
        return ret;
    }

    /// <summary>
    /// Walks the buffer as a sequence of FFXIVARR frames. For each frame we either read the
    /// keepalive precisely (when the frame is uncompressed) or detect it by shape (a single
    /// segment with a 0x18 decompressed length). Strictly bounds checked: a bad length can never
    /// make us read past <paramref name="len"/>.
    /// </summary>
    private void Scan(nuint socket, byte* p, int len, bool outgoing)
    {
        int o = 0;
        while (o + FfxivFrame.PacketHeaderSize <= len)
        {
            uint size = FfxivFrame.ReadU32(p, o + FfxivFrame.OffPacketSize);
            if (size < FfxivFrame.PacketHeaderSize || size > FfxivFrame.MaxFrameSize || o + (int)size > len)
                return; // not a clean frame boundary (mid-stream remnant); stop scanning this buffer

            byte compression = p[o + FfxivFrame.OffCompression];
            ushort segCount = FfxivFrame.ReadU16(p, o + FfxivFrame.OffSegmentCount);
            uint declen = FfxivFrame.ReadU32(p, o + FfxivFrame.OffDecompressedLen);

            if (compression == 0 && segCount >= 1 && segCount <= FfxivFrame.MaxSegmentCount)
            {
                // Precise path: the frame is plaintext, so read the real segment type and id.
                ScanSegmentsUncompressed(p, o, (int)size, outgoing);
            }
            else if (segCount == 1 && declen == FfxivFrame.KeepAliveDecompressedLen && IsMagicZero(p, o))
            {
                // Heuristic path: a compressed single-segment frame of keepalive shape (zero
                // magic, one segment, 0x18 decompressed). We cannot read the type/id, so pair an
                // outgoing keepalive with the next incoming one on the SAME socket (the response
                // always returns on the socket it was sent on). An incoming keepalive with no
                // outstanding send on its socket is server-initiated and is simply ignored.
                KeepAlivesSeen++;
                long now = Stopwatch.GetTimestamp();
                if (outgoing)
                {
                    lock (pendingGate) heuristicPending[socket] = now;
                }
                else
                {
                    long sentAt = 0;
                    lock (pendingGate)
                    {
                        if (heuristicPending.Remove(socket, out var t)) sentAt = t;
                    }
                    if (sentAt != 0)
                        monitor.AddSample((now - sentAt) * 1000.0 / Stopwatch.Frequency);
                }
            }

            o += (int)size;
        }
    }

    /// <summary>True if the 16-byte FFXIVARR magic at the frame start is all zero (keepalive frames are).</summary>
    private static bool IsMagicZero(byte* p, int o)
    {
        for (int i = 0; i < 16; i++)
            if (p[o + i] != 0) return false;
        return true;
    }

    private void ScanSegmentsUncompressed(byte* p, int o, int size, bool outgoing)
    {
        int so = o + FfxivFrame.PacketHeaderSize;
        int frameEnd = o + size;
        ushort segCount = FfxivFrame.ReadU16(p, o + FfxivFrame.OffSegmentCount);
        for (int s = 0; s < segCount && so + FfxivFrame.SegmentHeaderSize <= frameEnd; s++)
        {
            uint segSize = FfxivFrame.ReadU32(p, so + FfxivFrame.OffSegmentSize);
            if (segSize < FfxivFrame.SegmentHeaderSize || so + (int)segSize > frameEnd)
                break;

            ushort type = FfxivFrame.ReadU16(p, so + FfxivFrame.OffSegmentType);
            if ((type == FfxivFrame.SegmentKeepAlive || type == FfxivFrame.SegmentKeepAliveResponse)
                && segSize >= FfxivFrame.MinKeepAliveSegment)
            {
                uint id = FfxivFrame.ReadU32(p, so + FfxivFrame.OffKeepAliveId);
                KeepAlivesSeen++;
                if (outgoing && type == FfxivFrame.SegmentKeepAlive)
                    RecordSent(id);
                else if (!outgoing && type == FfxivFrame.SegmentKeepAliveResponse)
                    RecordResponse(id);
            }

            so += (int)segSize;
        }
    }

    // --- precise pairing (id available; only when a frame is uncompressed) ---

    private void RecordSent(uint id)
    {
        long now = Stopwatch.GetTimestamp();
        lock (pendingGate)
        {
            if (pending.Count >= MaxPending) pending.Clear();
            pending[id] = now;
        }
    }

    private void RecordResponse(uint id)
    {
        long now = Stopwatch.GetTimestamp();
        long sentAt;
        lock (pendingGate)
        {
            if (!pending.Remove(id, out sentAt))
                return;
        }
        Sample(now, sentAt);
    }

    private void Sample(long now, long sentAt)
    {
        monitor.AddSample((now - sentAt) * 1000.0 / Stopwatch.Frequency);
    }

    public void Dispose()
    {
        // Hook<T>.Dispose() disables the hook before tearing it down.
        recvHook?.Dispose();
        sendHook?.Dispose();
        wsaRecvHook?.Dispose();
        wsaSendHook?.Dispose();
        if (ws2Handle != nint.Zero)
            NativeLibrary.Free(ws2Handle);
        lock (pendingGate) pending.Clear();
    }
}
