using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace TruePing.Windows;

/// <summary>
/// The floating ping readout. A small, auto-sized ImGui window the user places anywhere;
/// it can be locked (click-through, no move/resize) for streaming or screenshots.
/// </summary>
public sealed class OverlayWindow : Window
{
    private readonly Plugin plugin;
    private readonly float[] history = new float[120];

    public OverlayWindow(Plugin plugin)
        : base("TruePing##TruePingOverlay")
    {
        this.plugin = plugin;
        RespectCloseHotkey = false;
        DisableWindowSounds = true;
    }

    public override void PreDraw()
    {
        var config = plugin.Config;
        var flags = ImGuiWindowFlags.NoTitleBar
                    | ImGuiWindowFlags.NoScrollbar
                    | ImGuiWindowFlags.NoCollapse
                    | ImGuiWindowFlags.AlwaysAutoResize
                    | ImGuiWindowFlags.NoFocusOnAppearing
                    | ImGuiWindowFlags.NoNav;

        if (!config.OverlayBackground)
            flags |= ImGuiWindowFlags.NoBackground;
        if (config.LockOverlay)
            flags |= ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoInputs;

        Flags = flags;
    }

    public override void Draw()
    {
        var config = plugin.Config;
        var stats = plugin.Monitor.Snapshot(config.WindowSeconds);

        if (config.OverlayScale != 1.0f)
            ImGui.SetWindowFontScale(config.OverlayScale);

        if (!stats.HasData)
        {
            ImGui.TextColored(PingColors.Stale, "TruePing  --");
            ImGui.TextDisabled(plugin.SourceInstalled
                ? "waiting for the first keepalive..."
                : "network hook not installed (see /trueping)");
        }
        else
        {
            var stale = stats.AgeSeconds > 12;
            var color = stale ? PingColors.Stale : PingColors.For(stats.CurrentMs, config);
            BoldText(color, $"{stats.CurrentMs:0} ms");

            // Minimal mode: the current ping and nothing else.
            if (!config.MinimalOverlay)
            {
                var line2 = $"avg {stats.AvgMs:0}  min {stats.MinMs:0}  max {stats.MaxMs:0}";
                if (config.ShowJitter)
                    line2 += $"   ±{stats.JitterMs:0}";
                ImGui.SameLine();
                ImGui.TextDisabled(line2);

                if (config.ShowLoss)
                {
                    var lossColor = stats.LossPercent <= 0 ? PingColors.Stale
                        : stats.LossPercent >= 2 ? PingColors.Bad : PingColors.Warn;
                    ImGui.TextColored(lossColor, $"loss {stats.LossPercent:0.0}%");
                }

                if (config.ShowGraph)
                {
                    int n = plugin.Monitor.GetHistory(history);
                    if (n > 1)
                    {
                        // ImGui binding-sensitive call; see .dev/UPDATING.md if it stops compiling.
                        ImGui.PlotLines("##tp_hist", history.AsSpan(0, n), n, string.Empty,
                            (float)stats.MinMs, (float)stats.MaxMs, new Vector2(180, 40));
                    }
                }
            }
        }

        if (config.OverlayScale != 1.0f)
            ImGui.SetWindowFontScale(1.0f);
    }

    /// <summary>
    /// Draws bold text. Dalamud's default font has no bold weight, so we fake it by stamping the
    /// glyphs twice with a one-pixel horizontal offset (using the current font and size, so it
    /// honours the overlay scale), then advance the layout cursor as a normal text item would.
    /// </summary>
    private static void BoldText(Vector4 color, string text)
    {
        var drawList = ImGui.GetWindowDrawList();
        var font = ImGui.GetFont();
        var fontSize = ImGui.GetFontSize();
        var pos = ImGui.GetCursorScreenPos();
        var col = ImGui.GetColorU32(color);

        drawList.AddText(font, fontSize, pos, col, text);
        drawList.AddText(font, fontSize, new Vector2(pos.X + 1f, pos.Y), col, text);

        ImGui.Dummy(ImGui.CalcTextSize(text));
    }
}
