using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace TruePing.Windows;

public sealed class ConfigWindow : Window
{
    private readonly Plugin plugin;

    public ConfigWindow(Plugin plugin)
        : base("TruePing###TruePingConfig")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 360),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        var config = plugin.Config;

        DrawLiveReadout(config);
        ImGui.Separator();
        DrawOverlaySection(config);
        ImGui.Separator();
        DrawThresholdSection(config);
        ImGui.Separator();
        DrawDiagnostics();
    }

    private void DrawLiveReadout(Configuration config)
    {
        var stats = plugin.Monitor.Snapshot(config.WindowSeconds);
        if (!stats.HasData)
        {
            ImGui.TextColored(PingColors.Stale, "True ping: waiting for data...");
            return;
        }

        var color = stats.AgeSeconds > 12 ? PingColors.Stale : PingColors.For(stats.CurrentMs, config);
        ImGui.TextColored(color, $"True ping: {stats.CurrentMs:0} ms");
        ImGui.TextDisabled(
            $"avg {stats.AvgMs:0} ms   min {stats.MinMs:0}   max {stats.MaxMs:0}   " +
            $"jitter ±{stats.JitterMs:0}   loss {stats.LossPercent:0.0}%   ({stats.SampleCount} samples)");
        ImGui.TextDisabled("This is the application round-trip the server actually answers. The latency you feel.");
    }

    private void DrawOverlaySection(Configuration config)
    {
        var showOverlay = config.ShowOverlay;
        if (ImGui.Checkbox("Show floating overlay", ref showOverlay))
        {
            config.ShowOverlay = showOverlay;
            config.Save();
        }

        var lockOverlay = config.LockOverlay;
        if (ImGui.Checkbox("Lock overlay (no move, click-through)", ref lockOverlay))
        {
            config.LockOverlay = lockOverlay;
            config.Save();
        }

        var minimal = config.MinimalOverlay;
        if (ImGui.Checkbox("Minimal: show only the current ping", ref minimal))
        {
            config.MinimalOverlay = minimal;
            config.Save();
        }

        var background = config.OverlayBackground;
        if (ImGui.Checkbox("Overlay background", ref background))
        {
            config.OverlayBackground = background;
            config.Save();
        }

        var jitter = config.ShowJitter;
        if (ImGui.Checkbox("Show jitter", ref jitter))
        {
            config.ShowJitter = jitter;
            config.Save();
        }
        ImGui.SameLine();
        var loss = config.ShowLoss;
        if (ImGui.Checkbox("Show packet loss", ref loss))
        {
            config.ShowLoss = loss;
            config.Save();
        }
        ImGui.SameLine();
        var graph = config.ShowGraph;
        if (ImGui.Checkbox("Show graph", ref graph))
        {
            config.ShowGraph = graph;
            config.Save();
        }

        var scale = config.OverlayScale;
        if (ImGui.SliderFloat("Overlay scale", ref scale, 0.7f, 2.5f, "%.1fx"))
        {
            config.OverlayScale = scale;
            config.Save();
        }

        var dtr = config.ShowDtrBar;
        if (ImGui.Checkbox("Show in the server info bar (top-right)", ref dtr))
        {
            config.ShowDtrBar = dtr;
            config.Save();
        }
    }

    private void DrawThresholdSection(Configuration config)
    {
        ImGui.TextDisabled("Colour thresholds (ms) and averaging window");

        var window = config.WindowSeconds;
        if (ImGui.SliderInt("Window (seconds)", ref window, 5, 120))
        {
            config.WindowSeconds = Math.Clamp(window, 5, 120);
            config.Save();
        }

        var good = config.GoodMs;
        if (ImGui.InputInt("Green at/below", ref good))
        {
            config.GoodMs = Math.Clamp(good, 1, 9999);
            config.Save();
        }

        var ok = config.OkMs;
        if (ImGui.InputInt("Yellow at/below", ref ok))
        {
            config.OkMs = Math.Clamp(ok, 1, 9999);
            config.Save();
        }

        var bad = config.BadMs;
        if (ImGui.InputInt("Red above", ref bad))
        {
            config.BadMs = Math.Clamp(bad, 1, 9999);
            config.Save();
        }
    }

    private void DrawDiagnostics()
    {
        ImGui.TextDisabled("Diagnostics");

        if (plugin.SourceInstalled)
            ImGui.TextColored(PingColors.Good, "Network hook: installed");
        else
            ImGui.TextColored(PingColors.Bad, "Network hook: NOT installed (see /xllog)");

        ImGui.TextDisabled($"Keepalive segments seen: {plugin.KeepAlivesSeen}");
        if (plugin.SourceInstalled && plugin.KeepAlivesSeen == 0)
        {
            ImGui.TextWrapped(
                "No keepalives observed yet. They are sparse (every few seconds), so give it a moment " +
                "in-game. If it stays at zero after a minute of being logged in, the game may be using " +
                "WSARecv/WSASend instead of recv/send on your setup; see .dev/UPDATING.md.");
        }

        if (ImGui.Button("Reset statistics"))
            plugin.Monitor.Reset();
    }
}
