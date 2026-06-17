using System;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using TruePing.Network;
using TruePing.Windows;

namespace TruePing;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/trueping";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IDtrBar DtrBar { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;

    public Configuration Config { get; }

    /// <summary>Live ping statistics, shared between the network thread and the UI.</summary>
    public PingMonitor Monitor { get; } = new();

    /// <summary>
    /// Consolidated stats, refreshed a few times a second on the framework thread. The overlay and
    /// the info bar read this instead of recomputing from the sample history every single frame.
    /// </summary>
    public PingStats Stats { get; private set; } = PingStats.Empty;

    /// <summary>Diagnostics: is the keepalive hook in place and seeing traffic?</summary>
    public bool SourceInstalled => source?.Installed ?? false;
    public long KeepAlivesSeen => source?.KeepAlivesSeen ?? 0;

    private readonly WindowSystem windowSystem = new("TruePing");
    private readonly ConfigWindow configWindow;
    private readonly OverlayWindow overlayWindow;
    private KeepAlivePingSource? source;
    private IDtrBarEntry? dtrEntry;
    private long lastStatsTick;
    private string lastDtrText = string.Empty;

    public Plugin()
    {
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        configWindow = new ConfigWindow(this);
        overlayWindow = new OverlayWindow(this);
        windowSystem.AddWindow(configWindow);
        windowSystem.AddWindow(overlayWindow);

        try
        {
            // Registering the server-info-bar entry and the command can both fail (for example
            // if another instance of the plugin already holds the "TruePing" title or /trueping).
            // Do them before installing the network hooks, so a failure here cannot leak hooks.
            dtrEntry = DtrBar.Get("TruePing");
            dtrEntry.OnClick = _ => configWindow.Toggle();

            CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "Opens TruePing. Subcommands: overlay, dtr, reset.",
            });

            // The network hooks are the only OS-level resource, so they go last.
            source = new KeepAlivePingSource(Monitor);

            PluginInterface.UiBuilder.Draw += windowSystem.Draw;
            PluginInterface.UiBuilder.OpenConfigUi += configWindow.Toggle;
            PluginInterface.UiBuilder.OpenMainUi += configWindow.Toggle;
            Framework.Update += OnUpdate;
        }
        catch
        {
            // A construction that fails halfway must not leak the hooks or the bar entry.
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        Framework.Update -= OnUpdate;
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= configWindow.Toggle;
        PluginInterface.UiBuilder.OpenMainUi -= configWindow.Toggle;

        dtrEntry?.Remove();
        CommandManager.RemoveHandler(CommandName);
        windowSystem.RemoveAllWindows();
        source?.Dispose();
    }

    private void OnCommand(string command, string arguments)
    {
        switch (arguments.Trim().ToLowerInvariant())
        {
            case "overlay":
                Config.ShowOverlay = !Config.ShowOverlay;
                Config.Save();
                break;
            case "dtr":
                Config.ShowDtrBar = !Config.ShowDtrBar;
                Config.Save();
                break;
            case "reset":
                Monitor.Reset();
                break;
            default:
                configWindow.Toggle();
                break;
        }
    }

    private void OnUpdate(IFramework framework)
    {
        source?.Update();

        // Keepalives arrive only every few seconds, so refreshing the consolidated stats a few
        // times a second is plenty; the overlay and info bar read this cached snapshot, keeping
        // the per-frame cost on the render path negligible.
        var nowTick = Environment.TickCount64;
        if (nowTick - lastStatsTick >= 200)
        {
            Stats = Monitor.Snapshot(Config.WindowSeconds);
            lastStatsTick = nowTick;
        }

        var connected = ClientState.IsLoggedIn;
        overlayWindow.IsOpen = Config.ShowOverlay && connected;

        UpdateDtr(connected);
    }

    private void UpdateDtr(bool connected)
    {
        if (dtrEntry is null)
            return;

        if (!Config.ShowDtrBar || !connected)
        {
            dtrEntry.Shown = false;
            return;
        }

        var stats = Stats;
        dtrEntry.Shown = true;
        dtrEntry.Text = stats.HasData ? $" {stats.CurrentMs:0} ms" : " --";
    }
}
