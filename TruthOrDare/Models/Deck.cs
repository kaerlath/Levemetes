using System;
using System.Collections.Generic;

namespace TruthOrDare.Models;

public sealed class Deck
{
    public const int CurrentFormatVersion = 10;

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New Deck";
    public string Author { get; set; } = string.Empty;
    public DateTimeOffset? ImportedAtUtc { get; set; }
    public DateTimeOffset? LastEditedAtUtc { get; set; }
    public List<Card> Cards { get; set; } = [];
    public List<CustomArtworkAsset> CustomArtwork { get; set; } = [];
    public string CustomCardBackSha256 { get; set; } = string.Empty;
}
