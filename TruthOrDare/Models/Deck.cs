using System;
using System.Collections.Generic;

namespace TruthOrDare.Models;

public sealed class Deck
{
    public const int CurrentFormatVersion = 9;

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New Deck";
    public string Author { get; set; } = string.Empty;
    public List<Card> Cards { get; set; } = [];
    public List<CustomArtworkAsset> CustomArtwork { get; set; } = [];
    public string CustomCardBackSha256 { get; set; } = string.Empty;
}
