# Update guide (game patch / Dalamud update)

Maintainer notes. Users do not need anything in this folder.

This file covers **fixing the plugin** when the game or Dalamud changes. For **shipping a
release to users** (version bump, GitHub release, repo.json), see `.dev/RELEASING.md` in the
[doomzao/plugins](https://github.com/doomzao/plugins) repository.

## TL;DR: what to do after each patch

```
1. Launch the game through XIVLauncher and let Dalamud update itself.
2. Plugin loads and the ping updates in-game        -> do nothing. (common case)
3. Plugin no longer compiles                        -> "Dalamud bump" / "ImGui bindings" below.
4. Compiles, loads, but the ping never appears      -> "No keepalives seen" below.
```

## Map of patch-sensitive dependencies

| Dependency | Maintained by | Where it lives | Breaks when |
|---|---|---|---|
| `Dalamud.NET.Sdk/15.0.0` | goatcorp | `TruePing/TruePing.csproj` (line 1) | Dalamud bumps its major version (new API level) |
| Dalamud API (`UiBuilder`, `Window`, `IDtrBar`, `IGameInteropProvider`, ImGui bindings) | goatcorp | `Plugin.cs`, `Windows/*` | Breaking changes in a major Dalamud release |
| `Hook<T>` / `HookFromAddress` | goatcorp | `Network/KeepAlivePingSource.cs` | Rarely |
| Winsock exports `recv`/`send`/`WSARecv`/`WSASend` | Microsoft (OS) | `KeepAlivePingSource` (NativeLibrary) | Never (stable OS ABI) |
| FFXIVARR frame header layout | **us** | `Network/FfxivFrame.cs` | If SE reshapes the low-level packet header |
| Keepalive frame shape (1 segment, `0x18` decompressed, zero magic) | **us** (observed) | `FfxivFrame.cs`, `Scan` heuristic | If SE changes the keepalive frame format |

Note what is **not** on this list: per-patch game signatures and IPC opcodes. The plugin uses
neither. The keepalive lives at the **segment** layer, below IPC, so the opcode shuffles each
patch do not touch it, and we never scan a game function so there is no signature to refresh.

## 1. Dalamud bump (new API level)

**Symptom**: Dalamud disables the plugin for an outdated API level, or `dotnet build` fails with
API errors.

Since Dalamud v9, API level = major version. For each major:

1. Find the new version: <https://dalamud.dev/versions/> (or the SDK version in
   [SamplePlugin](https://github.com/goatcorp/SamplePlugin/blob/master/SamplePlugin/SamplePlugin.csproj)).
2. In `TruePing.csproj`, update the first line, e.g. `Dalamud.NET.Sdk/16.0.0`.
3. If the .NET version changed (the "What's New in vXX" page says): `winget install Microsoft.DotNet.SDK.XX`.
4. `dotnet build -c Release` and fix errors against the breaking changes at `https://dalamud.dev/versions/vXX/`.

### ImGui bindings

The `Dalamud.Bindings.ImGui` signatures shift occasionally. The spot most likely to need a tweak
is the sparkline in `Windows/OverlayWindow.cs`:

```csharp
ImGui.PlotLines("##tp_hist", history.AsSpan(0, n), n, string.Empty, min, max, new Vector2(180, 40));
```

If it stops compiling, open the `ImGui.PlotLines` overloads (F12) and re-match the argument
order. Everything else we use (Checkbox, SliderInt/Float, InputInt, Text*) is stable.

### Server info bar (DTR)

`IDtrBarEntry.OnClick` is `Action<DtrInteractionEvent>` (not a parameterless `Action`); `Text`
takes a `SeString` (a plain string converts implicitly). If the DTR API is reshaped, the fix is
in `Plugin.cs` (`dtrEntry` setup and `UpdateDtr`).

## 2. No keepalives seen (ping never appears)

**Symptom**: plugin loads, the config window's diagnostics show "Network hook: installed" but
"Keepalive segments seen: 0" after a minute of being logged in.

Work through, in order:

1. **Give it time.** Keepalives are sparse (every few seconds, per connection). Stand in-game
   logged into a world for a minute.
2. **Are the hooks firing at all?** Temporarily log a per-detour counter (`recv`/`send`/`WSARecv`/
   `WSASend`) in `KeepAlivePingSource` and read `/xllog`. If every counter stays at 0 while you
   clearly have traffic, the game is no longer reaching `ws2_32`'s exports the way we hook them
   (rare). If counters climb but `keepalives` stays 0, it is the frame shape, below.
3. **The keepalive frame shape changed.** The heuristic in `Scan` looks for a frame with
   `segmentCount == 1`, decompressed length `0x18`, and a zero magic. If SE reshaped the keepalive,
   re-derive the new shape with the logging below and update the constants in `FfxivFrame.cs`
   (`KeepAliveDecompressedLen`) and/or the `Scan` condition.

### Re-deriving the keepalive shape

Temporary logging is the fastest check. In `Scan`, for each frame log `size`, `segmentCount`,
`compression`, the decompressed length (offset `0x24`), and whether the magic is zero; tag each
line OUT/IN. Then read `/xllog` while logged in and idle. The keepalive shows up as a tiny frame
arriving on a regular cadence (a few seconds), one OUT closely followed by one IN, both with a
single segment and the same small decompressed length. Today that length is `0x18` (one `0x10`
segment header plus an 8-byte payload) and the magic is zero; if those values moved, update the
heuristic to match. Frames are Oodle-compressed (`compression == 2`), which is expected: we never
decompress, we only match the shape.

### How the pairing works (so you do not chase a non-bug)

Two connections (zone and chat) each send keepalives, so you will see two socket handles in the
logs, interleaved. An outgoing keepalive is paired with the next incoming one **on the same
socket**; an incoming keepalive with no outstanding send on its socket is server-initiated and is
ignored on purpose (it is not a missed sample). An uncompressed keepalive would instead be read
precisely (segment type 7/8 + id), but in practice every frame is compressed and the shape path is
what runs.

## 3. Structural changes worth a design review

- **Game moving off the `ws2_32` exports entirely** (a different transport or its own stack): the
  capture layer would need a new hook point; the keepalive concept stays valid.
- **Keepalive removed or replaced** at the protocol level: would require a different RTT primitive
  (an IPC ping/pong opcode, which is patch-fragile). Revisit the whole approach.

## Sources to follow

- Dalamud versions and breaking changes: <https://dalamud.dev/versions/>
- FFXIVClientStructs: <https://github.com/aers/FFXIVClientStructs>
- Reference template (always updated for the new SDK): <https://github.com/goatcorp/SamplePlugin>
- FFXIVARR frame/segment layout reference: Sapphire's `CommonNetwork.h`, and the Deucalion
  (`ff14wed/deucalion`) packet parser.
- The goatcorp Discord (#plugin-dev), when a patch is big and everything is in flux.
