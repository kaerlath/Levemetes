using System.Collections.Generic;
using TruthOrDare.Models;

namespace TruthOrDare.Content;

public static class PatchNotesContent
{
    public static readonly IReadOnlyList<PatchNoteRelease> Releases =
    [
        new("1.5.0", "August 22, 2026", "The Living Leveplate",
        [
            new("Presentation",
            [
                new("✦", "Animated Levemetes header", "A living gold-and-burgundy banner now carries drifting motes, subtle illumination, and occasional shooting stars.", PatchBadge.New),
                new("◐", "Reduce Motion", "The new Appearance menu can hold decorative animation still while preserving the redesigned header.", PatchBadge.New),
            ]),
            new("Guidance",
            [
                new("?", "Structured Help guide", "Game basics, deck creation, sharing, relay rooms, direct connections, scoring, Observer mode, and troubleshooting now live in a searchable-feeling sectioned guide.", PatchBadge.New),
                new("★", "What's New archive", "Release notes remain available inside the plugin and automatically call attention to unread updates.", PatchBadge.New),
            ]),
        ]),
        new("1.4.2", "August 17, 2026", "Observers at the Table",
        [
            new("Multiplayer",
            [
                new("◉", "Observer mode", "Players may watch without drawing, scoring, volunteering, or entering random selection; hosts may manage Observer status as needed.", PatchBadge.New),
                new("◆", "Scores preserved", "Previously earned points remain intact while a player observes and scoring resumes when they return to active play.", PatchBadge.Improved),
            ]),
        ]),
        new("1.4.1", "August 17, 2026", "A Fair Call for Volunteers",
        [
            new("Relay multiplayer",
            [
                new("⧉", "Copy private room code", "Private relay room codes can be copied directly to the clipboard.", PatchBadge.New),
                new("⚖", "Lag-tolerant volunteers", "The opening five-second volunteer pool fairly resolves near-simultaneous responses before returning to first response selection.", PatchBadge.Improved),
            ]),
        ]),
    ];

    public static PatchNoteRelease Current => Releases[0];
}
