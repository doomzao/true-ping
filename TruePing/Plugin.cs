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

    /// <summary>Diagnostics: is the keepalive hook in place and seeing traffic?</summary>
    public bool SourceInstalled => source.Installed;
    public long KeepAlivesSeen => source.KeepAlivesSeen;

    private readonly KeepAlivePingSource source;
    private readonly WindowSystem windowSystem = new("TruePing");
    private readonly ConfigWindow configWindow;
    private readonly OverlayWindow overlayWindow;
    private readonly IDtrBarEntry dtrEntry;

    public Plugin()
    {
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        source = new KeepAlivePingSource(Monitor);

        configWindow = new ConfigWindow(this);
        overlayWindow = new OverlayWindow(this);
        windowSystem.AddWindow(configWindow);
        windowSystem.AddWindow(overlayWindow);

        dtrEntry = DtrBar.Get("TruePing");
        dtrEntry.OnClick = _ => configWindow.Toggle();

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens TruePing. Subcommands: overlay, dtr, reset.",
        });

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += configWindow.Toggle;
        PluginInterface.UiBuilder.OpenMainUi += configWindow.Toggle;
        Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        Framework.Update -= OnUpdate;
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= configWindow.Toggle;
        PluginInterface.UiBuilder.OpenMainUi -= configWindow.Toggle;

        dtrEntry.Remove();
        CommandManager.RemoveHandler(CommandName);
        windowSystem.RemoveAllWindows();
        source.Dispose();
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
        source.Update();

        var connected = ClientState.IsLoggedIn;
        overlayWindow.IsOpen = Config.ShowOverlay && connected;

        UpdateDtr(connected);
    }

    private void UpdateDtr(bool connected)
    {
        if (!Config.ShowDtrBar || !connected)
        {
            dtrEntry.Shown = false;
            return;
        }

        var stats = Monitor.Snapshot(Config.WindowSeconds);
        dtrEntry.Shown = true;
        dtrEntry.Text = stats.HasData ? $" {stats.CurrentMs:0} ms" : " --";
    }
}
