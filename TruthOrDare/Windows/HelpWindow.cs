using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using TruthOrDare.Content;
using TruthOrDare.Windows.Components;

namespace TruthOrDare.Windows;

public sealed class HelpWindow(Configuration configuration) : Window("Levemetes Help & User Guide##LevemetesHelp")
{
    public override void Draw()
    {
        var version = VersionText();
        LevemetesHeader.Draw(HelpContent.UserGuide.Subtitle, version, LevemetesHeaderMode.Full, configuration.ReduceMotion);
        ImGui.BeginChild("HelpScroll", new Vector2(-1, -1), false);
        DocumentationRenderer.Draw(HelpContent.UserGuide);
        ImGui.EndChild();
    }

    public override void PreDraw()
    {
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(680, 650), MaximumSize = new Vector2(float.MaxValue) };
        MainWindow.PushLevemetesTheme();
    }
    public override void PostDraw() => MainWindow.PopLevemetesTheme();
    private static string VersionText()
    {
        var version = typeof(Plugin).Assembly.GetName().Version;
        return version is null ? string.Empty : $"v{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
    }
}
