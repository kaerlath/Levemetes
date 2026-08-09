using System;

namespace TruthOrDare.Models;

public sealed class CustomArtworkAsset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Custom Artwork";
    public string Sha256 { get; set; } = string.Empty;
}
