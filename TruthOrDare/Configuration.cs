using System;
using Dalamud.Configuration;
using TruthOrDare.Models;

namespace TruthOrDare;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public Guid? SelectedDeckId { get; set; }
    public CardCategory SelectedCategory { get; set; } = CardCategory.Sfw;
}
