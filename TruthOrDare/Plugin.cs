using System;
using System.IO;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using TruthOrDare.Services;
using TruthOrDare.Windows;

namespace TruthOrDare;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/levemetes";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;

    private readonly WindowSystem windowSystem = new("Levemetes");
    private readonly MainWindow mainWindow;
    private readonly DirectGameService directGame;
    private readonly RelayGameService relayGame;

    public Plugin()
    {
        var configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        var store = new DeckStore(PluginInterface.GetPluginConfigDirectory(),
            (exception, message, file) => Log.Warning(exception, message, file));
        directGame = new DirectGameService((exception, message) => Log.Warning(exception, message));
        relayGame = new RelayGameService();
        var cardBackPath = Path.Combine(PluginInterface.AssemblyLocation.Directory?.FullName!, "card-back.png");
        var templateDirectory = Path.Combine(PluginInterface.AssemblyLocation.Directory?.FullName!, "Assets", "Templates");
        var artworkDirectory = Path.Combine(PluginInterface.AssemblyLocation.Directory?.FullName!, "Assets", "Artwork");
        mainWindow = new MainWindow(configuration, store, directGame, relayGame, SaveConfiguration, cardBackPath, templateDirectory, artworkDirectory);
        windowSystem.AddWindow(mainWindow);

        CommandManager.AddHandler(Command, new CommandInfo(OnCommand) { HelpMessage = "Open Levemetes." });
        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += OpenMainUi;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenMainUi;
        CommandManager.RemoveHandler(Command);
        windowSystem.RemoveAllWindows();
        mainWindow.Dispose();
        directGame.Dispose();
        relayGame.Dispose();
    }

    private static void SaveConfiguration(Configuration configuration) => PluginInterface.SavePluginConfig(configuration);
    private void OnCommand(string _, string arguments) => mainWindow.IsOpen = true;
    private void OpenMainUi() => mainWindow.IsOpen = true;
}
