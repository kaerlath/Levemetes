using System;
using System.Collections.Generic;

namespace TruthOrDare.Models;

public sealed class Deck
{
    public const int CurrentFormatVersion = 5;

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New Deck";
    public List<Card> Cards { get; set; } = [];
}
