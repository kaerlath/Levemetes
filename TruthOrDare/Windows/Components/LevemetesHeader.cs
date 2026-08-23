using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace TruthOrDare.Windows.Components;

internal enum LevemetesHeaderMode { Compact, Full }

internal static class LevemetesHeader
{
    private static readonly Vector4 Gold = new(.72f, .56f, .27f, 1f);
    private static readonly Vector4 BrightGold = new(.96f, .80f, .46f, 1f);
    private static float nextDealAt = 2.8f;
    private static float dealStartedAt = -10f;
    private static float dealY = .28f;
    private static uint sequence;

    internal static void Draw(string subtitle, string version, LevemetesHeaderMode mode, bool reduceMotion)
    {
        var full = mode == LevemetesHeaderMode.Full;
        var scale = ImGui.GetIO().FontGlobalScale;
        var height = (full ? 132f : 86f) * scale;
        var width = ImGui.GetContentRegionAvail().X;
        var topLeft = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton($"##LevemetesHeader-{mode}-{subtitle}", new Vector2(width, height));
        var bottomRight = topLeft + new Vector2(width, height);
        var draw = ImGui.GetWindowDrawList();
        var time = reduceMotion ? 0f : (float)ImGui.GetTime();
        var breath = reduceMotion ? .35f : .5f + .5f * MathF.Sin(time * .68f);

        draw.AddRectFilled(topLeft, bottomRight, ImGui.ColorConvertFloat4ToU32(new Vector4(.035f, .015f, .021f, .97f)), 7);
        draw.AddRect(topLeft, bottomRight, ImGui.ColorConvertFloat4ToU32(new Vector4(Gold.X, Gold.Y, Gold.Z, .52f + breath * .28f)), 7, default, 1.5f);
        draw.AddRect(topLeft + new Vector2(4, 4), bottomRight - new Vector2(4, 4),
            ImGui.ColorConvertFloat4ToU32(new Vector4(.42f, .12f, .17f, .62f)), 5, default, 1);
        DrawFiligree(draw, topLeft, bottomRight, breath);
        DrawCardMotes(draw, topLeft, bottomRight, time, full, !reduceMotion);
        if (!reduceMotion) { UpdateDealtCard(time); DrawDealtCard(draw, topLeft, bottomRight, time); }

        var title = "L E V E M E T E S";
        var titleSize = ImGui.CalcTextSize(title) * (full ? 1.55f : 1.30f);
        var titlePosition = new Vector2(topLeft.X + (width - titleSize.X) / 2, topLeft.Y + (full ? 34 : 19) * scale);
        draw.AddText(ImGui.GetFont(), ImGui.GetFontSize() * (full ? 1.55f : 1.30f), titlePosition + new Vector2(1.5f, 1.5f),
            ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, .72f)), title);
        draw.AddText(ImGui.GetFont(), ImGui.GetFontSize() * (full ? 1.55f : 1.30f), titlePosition,
            ImGui.ColorConvertFloat4ToU32(new Vector4(BrightGold.X, BrightGold.Y, BrightGold.Z, .90f + breath * .10f)), title);

        var subtitleSize = ImGui.CalcTextSize(subtitle);
        draw.AddText(new Vector2(topLeft.X + (width - subtitleSize.X) / 2, bottomRight.Y - 23 * scale),
            ImGui.ColorConvertFloat4ToU32(new Vector4(.82f, .76f, .65f, 1)), subtitle);
        if (!string.IsNullOrWhiteSpace(version))
        {
            var versionSize = ImGui.CalcTextSize(version);
            draw.AddText(new Vector2(bottomRight.X - versionSize.X - 12 * scale, topLeft.Y + 10 * scale),
                ImGui.ColorConvertFloat4ToU32(new Vector4(.65f, .58f, .48f, 1)), version);
        }
        ImGui.Spacing();
    }

    private static void DrawFiligree(ImDrawListPtr draw, Vector2 minimum, Vector2 maximum, float breath)
    {
        var color = ImGui.ColorConvertFloat4ToU32(new Vector4(Gold.X, Gold.Y, Gold.Z, .38f + breath * .16f));
        var center = (minimum.X + maximum.X) / 2;
        draw.AddLine(minimum + new Vector2(15, 13), new Vector2(center - 72, minimum.Y + 13), color, 1);
        draw.AddLine(new Vector2(center + 72, minimum.Y + 13), maximum - new Vector2(15, maximum.Y - minimum.Y - 13), color, 1);
        draw.AddCircleFilled(new Vector2(center, minimum.Y + 13), 3, color);
        draw.AddCircle(new Vector2(center, minimum.Y + 13), 7, color, 8, 1);
        draw.AddLine(minimum + new Vector2(15, maximum.Y - minimum.Y - 13), new Vector2(center - 72, maximum.Y - 13), color, 1);
        draw.AddLine(new Vector2(center + 72, maximum.Y - 13), maximum - new Vector2(15, 13), color, 1);
    }

    private static void DrawCardMotes(ImDrawListPtr draw, Vector2 minimum, Vector2 maximum, float time, bool full, bool motion)
    {
        var count = full ? 13 : 8;
        var width = maximum.X - minimum.X;
        var height = maximum.Y - minimum.Y;
        for (var i = 0; i < count; i++)
        {
            var hx = Hash((uint)(i * 37 + 11)); var hy = Hash((uint)(i * 53 + 23));
            var x = minimum.X + 10 + hx / (float)uint.MaxValue * MathF.Max(1, width - 20);
            var y = minimum.Y + 8 + hy / (float)uint.MaxValue * MathF.Max(1, height - 16);
            if (motion)
            {
                y += MathF.Sin(time * (.22f + i % 3 * .05f) + i * 1.7f) * 3.5f;
                x += MathF.Cos(time * .16f + i * 2.1f) * 2.5f;
            }

            var alpha = i % 3 == 0 ? .20f : .12f;
            var halfWidth = i % 4 == 0 ? 4.5f : 3.5f;
            var halfHeight = halfWidth * 1.35f;
            var center = new Vector2(x, y);
            var angle = (i % 2 == 0 ? 1 : -1) * (.10f + i % 5 * .035f);
            DrawCardSilhouette(draw, center, halfWidth, halfHeight, angle, alpha);
        }
    }

    private static void UpdateDealtCard(float time)
    {
        if (time < nextDealAt) return;
        dealStartedAt = time; sequence++;
        var hash = Hash(sequence * 7919u);
        dealY = .18f + (hash & 0xffff) / 65535f * .34f;
        nextDealAt = time + 6.5f + Hash(hash) % 5000 / 1000f;
    }

    private static void DrawDealtCard(ImDrawListPtr draw, Vector2 minimum, Vector2 maximum, float time)
    {
        var age = time - dealStartedAt;
        const float duration = 2.1f;
        if (age is < 0 or > duration) return;
        var progress = age / duration;
        var eased = progress * progress * (3f - 2f * progress);
        var center = new Vector2(minimum.X - 18 + eased * (maximum.X - minimum.X + 36),
            minimum.Y + (maximum.Y - minimum.Y) * dealY + MathF.Sin(progress * MathF.PI) * 9f);
        var fade = MathF.Sin(progress * MathF.PI);
        DrawCardSilhouette(draw, center, 7f, 10f, -.42f + progress * .85f, fade * .82f);
    }

    private static void DrawCardSilhouette(ImDrawListPtr draw, Vector2 center, float halfWidth, float halfHeight, float angle, float alpha)
    {
        var cosine = MathF.Cos(angle);
        var sine = MathF.Sin(angle);
        Vector2 Rotate(float x, float y) => center + new Vector2(x * cosine - y * sine, x * sine + y * cosine);

        var a = Rotate(-halfWidth, -halfHeight);
        var b = Rotate(halfWidth, -halfHeight);
        var c = Rotate(halfWidth, halfHeight);
        var d = Rotate(-halfWidth, halfHeight);
        draw.AddQuadFilled(a, b, c, d, ImGui.ColorConvertFloat4ToU32(new Vector4(.24f, .055f, .075f, alpha * .72f)));
        draw.AddQuad(a, b, c, d, ImGui.ColorConvertFloat4ToU32(new Vector4(BrightGold.X, BrightGold.Y, BrightGold.Z, alpha)), 1f);

        var pip = Rotate(0, 0);
        var pipColor = ImGui.ColorConvertFloat4ToU32(new Vector4(.96f, .75f, .34f, alpha * .95f));
        draw.AddQuadFilled(pip + new Vector2(0, -2.2f), pip + new Vector2(2.2f, 0), pip + new Vector2(0, 2.2f), pip + new Vector2(-2.2f, 0), pipColor);
    }

    private static uint Hash(uint value)
    {
        value ^= value >> 16; value *= 0x7feb352du; value ^= value >> 15; value *= 0x846ca68bu;
        return value ^ value >> 16;
    }
}
