# Architecture

Maintainer notes. Users do not need anything in this folder.

## Goal

Report the **true** ping: the application-layer round-trip the FFXIV server actually answers,
and do it robustly even when the player routes through a local proxy or VPN (ExitLag, Mudfish,
NoPing, WTFast, a plain VPN), the case that defeats OS-level RTT readers.

## Why not the OS socket RTT

The obvious approach, and what most ping plugins do, is to find the game's TCP connection and
read its smoothed RTT from Windows (`GetPerTcpConnectionEStats`) or ping the endpoint with ICMP.
That breaks the moment the player uses a tunnel: the game's socket connects to `127.0.0.1` (the
proxy's local listener), so the OS RTT of that socket is ~0 ms and the real server is hidden
behind the proxy. We confirmed this on the maintainer's own machine, with ExitLag listening on
`127.0.0.1:55504` and the game connected to it. A network-layer reader can only measure to
whatever endpoint the socket exposes, which under a tunnel is the wrong endpoint.

## The core idea

The FFXIV low-level frame protocol carries a **keepalive** handshake. At the protocol level it
is a request/response pair:

```
client  --- keepalive          (segment type 7, carries an id) -->  server
client  <-- keepalive response (segment type 8, same id)       --   server
```

We time that round-trip with a monotonic clock (`Stopwatch`). It includes everything in the real
path (the network, the proxy/VPN, and the server's own processing), so it is exactly the latency
the player feels, and it is the same number whether or not a tunnel is in use. This is the
application-layer latency, which is what gameplay actually experiences, not the network-layer RTT.

## How we observe the handshake

No game signatures. We hook the **Winsock exports** (`recv`, `send`, `WSARecv`, `WSASend` in
`ws2_32.dll`), obtained via `NativeLibrary.GetExport` and hooked with Dalamud's
`IGameInteropProvider.HookFromAddress`. The detours run on the game's network thread, call the
original, and scan the buffer for keepalive frames.

The catch: FFXIV compresses every frame on the wire with Oodle, and the compression is stateful
(it depends on a per-connection state established at login). Because the plugin loads mid-session
we cannot reconstruct that state, so we cannot decompress, and therefore cannot read the segment
type or the keepalive id at this layer.

Instead we identify a keepalive by the **shape** of its frame, which is unmistakable even while
compressed:

- exactly one segment (`segmentCount == 1`),
- a decompressed length of `0x18` (a 16-byte segment header plus the 8-byte keepalive payload),
- a zero magic.

We never decompress anything. An outgoing keepalive is paired with the **next incoming keepalive
on the same socket** (the response always returns on the socket the request left on). The
keepalive is strictly one-at-a-time per connection, so a single outstanding request per socket is
all we need to track.

```
send detours (send / WSASend):  scan outgoing buffer; on a keepalive-shaped frame,
                                 record heuristicPending[socket] = now
recv detours (recv / WSARecv):  scan incoming buffer; on a keepalive-shaped frame,
                                 rtt = now - heuristicPending[socket]; feed PingMonitor
main thread (Framework.Update):  expire a pending keepalive older than the timeout,
                                 count it as packet loss
```

(An uncompressed keepalive, should a patch ever ship one, is read precisely instead: real
segment type 7/8 and id, paired by id. That path is kept for robustness; in practice every frame
is compressed and the shape heuristic is what runs.)

### Why pair per socket

FFXIV holds more than one connection (zone and chat), and **both** send keepalives, interleaved.
Pairing globally could match a request on one connection with a response on the other; their
latencies are similar enough that the error would hide in plain sight. Keying the pending map by
socket handle makes a cross-connection mismatch impossible by construction. An incoming keepalive
with no outstanding request on its socket (a server-initiated keepalive) is simply ignored rather
than producing a bogus sample.

## Threads and ownership

| Thread | What it does | File |
|---|---|---|
| Game network | `recv`/`send`/`WSARecv`/`WSASend` detours: scan frames, time keepalives | `Network/KeepAlivePingSource.cs` |
| Main (framework) | timeout sweep, server-info-bar text, overlay visibility | `Plugin.OnUpdate`, `KeepAlivePingSource.Update` |
| Render (`UiBuilder.Draw`) | overlay and config window drawing | `Windows/*` |

`PingMonitor` is the one object shared between the network thread (which writes samples and loss)
and the UI/main thread (which reads snapshots). It guards everything with a single lock and keeps
the critical sections tiny, so the game's network thread never stalls on us. The pending-keepalive
maps live in the source with their own lock, touched by the detours and by the timeout sweep.

## Safety in the detours

The detours sit on the game's entire socket traffic, so two rules are absolute:

1. **Never throw.** Every detour wraps the scan in a bare `try/catch` and always calls the
   original. A parsing bug must never break the game's network.
2. **Never read past the buffer.** The frame walk (`Scan`) is strictly bounds-checked against the
   length the OS reported; a corrupt or mid-stream length stops the scan instead of walking into
   unrelated memory.

## Smaller decisions

- **Hook all four Winsock entry points.** FFXIV uses the overlapped `WSARecv`/`WSASend` on this
  client; `recv`/`send` are hooked too so the capture also works where those are used. Hooking the
  stable OS exports avoids any game signature, so nothing here breaks on a game patch.
- **Identify by shape, not by reading the packet.** Decompressing Oodle mid-session is impossible,
  and unnecessary: the keepalive frame's shape is a clean, specific fingerprint.
- **Jitter = standard deviation** of the samples in the window (not RFC 3550 interarrival), which
  reads naturally next to avg/min/max.
- **Loss = timed-out keepalives**, computed over the same time window as the latency: a keepalive
  sent with no response within ~6 s counts as one lost probe, and loss is `lost / (answered + lost)`
  for the window. It is a coarse but honest hiccup indicator, not a per-packet loss meter.
- **Stale marking.** Keepalives are sparse, so the UI greys out a reading older than ~12 s instead
  of pretending it is current.
- **No external dependencies** (no Machina, no Oodle library): the whole capture is the Winsock
  hooks plus a small bounds-checked frame parser, matching the project family's preference for
  minimal dependencies.

## The one patch-sensitive assumption

Everything above is signature-free, but it does assume the FFXIVARR frame header layout (where the
size, segment count, compression flag and decompressed length live) and the keepalive frame shape.
These are isolated in `Network/FfxivFrame.cs` and have been stable for years. See `UPDATING.md`.
