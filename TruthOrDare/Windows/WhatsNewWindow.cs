using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using TruthOrDare.Content;
using TruthOrDare.Windows.Components;

namespace TruthOrDare.Windows;

public sealed class WhatsNewWindow(Configuration configuration, Action<Configuration> saveConfiguration)
    : Window("Levemetes — What's New##LevemetesWhatsNew")
{
    private int selectedRelease;

    public override void Draw()
    {
        LevemetesHeader.Draw("What's new around the Levemetes table", VersionText(), LevemetesHeaderMode.Full, configuration.ReduceMotion);
        var release = PatchNotesContent.Releases[selectedRelease];
        ImGui.SetNextItemWidth(300);
        if (ImGui.BeginCombo("Release", $"{release.Version} — {release.Title}"))
        {
            for (var i = 0; i < PatchNotesContent.Releases.Count; i++)
                if (ImGui.Selectable($"{PatchNotesContent.Releases[i].Version} — {PatchNotesContent.Releases[i].Title}", i == selectedRelease)) selectedRelease = i;
            ImGui.EndCombo();
        }
        ImGui.BeginChild("PatchNotesScroll", new Vector2(-1, -46), false);
        PatchNotesRenderer.Draw(release);
        ImGui.EndChild();
        var unread = configuration.LastSeenPatchNotesVersion != PatchNotesContent.Current.Version;
        ImGui.BeginDisabled(!unread);
        if (ImGui.Button(unread ? "Mark Current Notes as Read" : "Current Notes Read", new Vector2(220, 32)))
        {
            configuration.LastSeenPatchNotesVersion = PatchNotesContent.Current.Version;
            saveConfiguration(configuration);
        }
        ImGui.EndDisabled(); ImGui.SameLine(); ImGui.TextDisabled("Release notes remain available here after they are read.");
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
