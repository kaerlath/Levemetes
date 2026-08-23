using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using TruthOrDare.Models;

namespace TruthOrDare.Windows.Components;

internal static class DocumentationRenderer
{
    internal static void Draw(HelpDocument document)
    {
        foreach (var section in document.Sections)
        {
            var flags = section.DefaultOpen ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;
            if (!ImGui.CollapsingHeader($"{section.Icon}  {section.Title}##{section.Id}", flags)) continue;
            ImGui.Indent(8);
            foreach (var block in section.Blocks) DrawBlock(block, section.Id);
            ImGui.Unindent(8); ImGui.Spacing();
        }
    }

    private static void DrawBlock(DocumentationBlock block, string sectionId)
    {
        if (block.Kind == DocumentationBlockKind.Paragraph) ImGui.TextWrapped(block.Text);
        else if (block.Kind == DocumentationBlockKind.Code)
        {
            ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(.025f, .020f, .024f, .96f));
            ImGui.BeginChild($"Code-{sectionId}-{block.Text.GetHashCode()}", new Vector2(-1, 38), true);
            ImGui.TextColored(new Vector4(.90f, .76f, .46f, 1), block.Text);
            ImGui.EndChild(); ImGui.PopStyleColor();
        }
        else DrawCallout(block);
        ImGui.Spacing();
    }

    private static void DrawCallout(DocumentationBlock block)
    {
        var (label, color) = block.Kind switch
        {
            DocumentationBlockKind.Tip => ("TIP", new Vector4(.38f, .72f, .56f, 1)),
            DocumentationBlockKind.Warning => ("WARNING", new Vector4(.92f, .61f, .30f, 1)),
            _ => ("IMPORTANT", new Vector4(.82f, .38f, .42f, 1)),
        };
        var available = MathF.Max(100, ImGui.GetContentRegionAvail().X - 24);
        var height = ImGui.CalcTextSize(block.Text, false, available).Y + ImGui.GetTextLineHeightWithSpacing() + 20;
        ImGui.PushStyleColor(ImGuiCol.Border, color);
        ImGui.BeginChild($"Callout-{block.GetHashCode()}", new Vector2(-1, height), true);
        ImGui.TextColored(color, label); ImGui.TextWrapped(block.Text);
        ImGui.EndChild(); ImGui.PopStyleColor();
    }
}
