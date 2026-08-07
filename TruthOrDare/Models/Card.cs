using System;

namespace TruthOrDare.Models;

public sealed class Card
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "Untitled Levemete";
    public ActivityType Activity { get; set; }
    public CardCategory Category { get; set; }
    public CardKeyword? Keyword { get; set; }
    public string Text { get; set; } = string.Empty;
    public string FlavorText { get; set; } = string.Empty;
}
