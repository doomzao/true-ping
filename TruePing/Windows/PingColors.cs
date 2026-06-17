using System.Numerics;

namespace TruePing.Windows;

/// <summary>Maps a latency in ms to a colour using the user's configured thresholds.</summary>
internal static class PingColors
{
    public static readonly Vector4 Good = new(0.40f, 0.85f, 0.40f, 1f);   // green
    public static readonly Vector4 Ok = new(0.95f, 0.85f, 0.30f, 1f);     // yellow
    public static readonly Vector4 Warn = new(0.98f, 0.60f, 0.25f, 1f);   // orange
    public static readonly Vector4 Bad = new(0.95f, 0.35f, 0.35f, 1f);    // red
    public static readonly Vector4 Stale = new(0.6f, 0.6f, 0.6f, 1f);     // grey

    public static Vector4 For(double ms, Configuration config)
    {
        if (ms <= config.GoodMs) return Good;
        if (ms <= config.OkMs) return Ok;
        if (ms <= config.BadMs) return Warn;
        return Bad;
    }
}
