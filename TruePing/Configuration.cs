using Dalamud.Configuration;

namespace TruePing;

[System.Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // --- Overlay ---
    /// <summary>Show the floating ping overlay.</summary>
    public bool ShowOverlay { get; set; } = true;

    /// <summary>Lock the overlay in place (no move, no resize), for a clean stream/screenshot.</summary>
    public bool LockOverlay { get; set; } = false;

    /// <summary>Show only the current ping in the overlay; hide average, jitter, loss and graph.</summary>
    public bool MinimalOverlay { get; set; } = false;

    /// <summary>Draw a translucent background behind the overlay text.</summary>
    public bool OverlayBackground { get; set; } = true;

    /// <summary>Show jitter (latency stability) next to the ping.</summary>
    public bool ShowJitter { get; set; } = true;

    /// <summary>Show packet loss percentage.</summary>
    public bool ShowLoss { get; set; } = true;

    /// <summary>Show a small history sparkline in the overlay.</summary>
    public bool ShowGraph { get; set; } = false;

    /// <summary>Overlay text scale.</summary>
    public float OverlayScale { get; set; } = 1.0f;

    // --- Server info bar (DTR) ---
    /// <summary>Show the ping in the server info bar (top-right, next to the clock).</summary>
    public bool ShowDtrBar { get; set; } = true;

    // --- Stats window / coloring ---
    /// <summary>Window (seconds) over which avg/min/max/jitter are computed.</summary>
    public int WindowSeconds { get; set; } = 30;

    /// <summary>At or below this many ms the ping is shown green ("good").</summary>
    public int GoodMs { get; set; } = 90;

    /// <summary>At or below this many ms the ping is shown yellow ("ok"); above it is orange/red.</summary>
    public int OkMs { get; set; } = 160;

    /// <summary>Above this many ms the ping is shown red ("bad").</summary>
    public int BadMs { get; set; } = 250;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
