using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using TruthOrDare.Models;

namespace TruthOrDare.Windows.Components;

internal static class PatchNotesRenderer
{
    internal static void Draw(PatchNoteRelease release)
    {
        ImGui.TextColored(new Vector4(.96f, .84f, .54f, 1), release.Title);
        ImGui.SameLine(); Badge(release.IsPrerelease ? "PRE-RELEASE" : "RELEASE", new Vector4(.48f, .30f, .38f, 1));
        ImGui.TextDisabled($"Version {release.Version} · {release.ReleaseDate}"); ImGui.Spacing();
        foreach (var section in release.Sections)
        {
            ImGui.TextColored(new Vector4(.94f, .72f, .38f, 1), section.Title.ToUpperInvariant());
            foreach (var item in section.Items)
            {
                ImGui.PushID($"{release.Version}-{section.Title}-{item.Title}");
                var textHeight = ImGui.CalcTextSize(item.Description, false, MathF.Max(100, ImGui.GetContentRegionAvail().X - 24)).Y;
                ImGui.BeginChild("PatchItem", new Vector2(-1, MathF.Max(76, textHeight + 43)), true);
                ImGui.TextColored(new Vector4(.95f, .82f, .52f, 1), $"{item.Icon}  {item.Title}");
                if (item.Badge is { } badge) { ImGui.SameLine(); Badge(badge.ToString().ToUpperInvariant(), BadgeColor(badge)); }
                ImGui.TextWrapped(item.Description); ImGui.EndChild(); ImGui.PopID();
            }
            ImGui.Spacing();
        }
    }

    private static void Badge(string text, Vector4 color)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, color); ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(5, 1));
        ImGui.SmallButton(text); ImGui.PopStyleVar(); ImGui.PopStyleColor();
    }

    private static Vector4 BadgeColor(PatchBadge badge) => badge switch
    {
        PatchBadge.New => new Vector4(.30f, .58f, .42f, 1),
        PatchBadge.Improved => new Vector4(.38f, .46f, .72f, 1),
        PatchBadge.Fixed => new Vector4(.58f, .47f, .27f, 1),
        _ => new Vector4(.70f, .35f, .35f, 1),
    };
}
