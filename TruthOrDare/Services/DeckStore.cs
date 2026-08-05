using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin.Services;
using TruthOrDare.Models;

namespace TruthOrDare.Services;

public sealed class DeckStore
{
    private const int MaxDecks = 100;
    private const int MaxCardsPerDeck = 5000;
    private const int MaxNameLength = 80;
    private const int MaxCardLength = 1000;
    private const int MaxTitleLength = 100;
    private readonly string decksDirectory;
    private readonly IPluginLog log;
    private readonly JsonSerializerOptions jsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public DeckStore(string configDirectory, IPluginLog log)
    {
        decksDirectory = Path.Combine(configDirectory, "decks");
        this.log = log;
    }

    public string DecksDirectory => decksDirectory;

    public List<Deck> LoadAll()
    {
        Directory.CreateDirectory(decksDirectory);
        var decks = new List<Deck>();
        foreach (var file in Directory.EnumerateFiles(decksDirectory, "*.json").Take(MaxDecks))
        {
            try
            {
                var deck = Read(file, preserveId: true);
                decks.Add(deck);
            }
            catch (Exception ex)
            {
                log.Warning(ex, "Could not load deck {File}", file);
            }
        }

        if (decks.Count == 0)
        {
            var starter = CreateStarterDeck();
            Save(starter);
            decks.Add(starter);
        }

        return decks.OrderBy(deck => deck.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void Save(Deck deck)
    {
        Validate(deck);
        Directory.CreateDirectory(decksDirectory);
        var destination = Path.Combine(decksDirectory, $"{deck.Id:N}.json");
        var temporary = destination + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(deck, jsonOptions));
        File.Move(temporary, destination, true);
    }

    public void Delete(Deck deck)
    {
        var path = Path.Combine(decksDirectory, $"{deck.Id:N}.json");
        if (File.Exists(path)) File.Delete(path);
    }

    public string Export(Deck deck, string requestedPath)
    {
        Validate(deck);
        var path = NormalizeJsonPath(requestedPath);
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("Choose a directory as well as a file name.");
        Directory.CreateDirectory(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(deck, jsonOptions));
        return path;
    }

    public Deck Import(string path)
    {
        var deck = ReadImport(path);
        Save(deck);
        return deck;
    }

    public (int Added, int Skipped) Merge(string path, Deck destination)
    {
        var imported = ReadImport(path);
        var existing = new HashSet<string>(destination.Cards.Select(CardFingerprint), StringComparer.Ordinal);
        var additions = new List<Card>();
        var skipped = 0;

        foreach (var card in imported.Cards)
        {
            if (!existing.Add(CardFingerprint(card)))
            {
                skipped++;
                continue;
            }

            card.Id = Guid.NewGuid();
            additions.Add(card);
        }

        var merged = new Deck
        {
            FormatVersion = destination.FormatVersion,
            Id = destination.Id,
            Name = destination.Name,
            Cards = [.. destination.Cards, .. additions],
        };
        Save(merged);
        destination.Cards = merged.Cards;
        return (additions.Count, skipped);
    }

    private Deck ReadImport(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Choose a JSON deck file.");
        var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
        if (!File.Exists(fullPath)) throw new FileNotFoundException("The selected JSON file does not exist.", fullPath);
        return Read(fullPath, preserveId: false);
    }

    private static string CardFingerprint(Card card) => string.Join('\u001f',
        NormalizeForComparison(card.Title),
        ((int)card.Activity).ToString(),
        ((int)card.Category).ToString(),
        card.Keyword?.ToString() ?? string.Empty,
        NormalizeForComparison(card.Text));

    private static string NormalizeForComparison(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    private Deck Read(string path, bool preserveId)
    {
        var info = new FileInfo(path);
        if (info.Length > 5 * 1024 * 1024) throw new InvalidDataException("Deck files must be smaller than 5 MB.");
        var deck = JsonSerializer.Deserialize<Deck>(File.ReadAllText(path), jsonOptions)
            ?? throw new InvalidDataException("The file does not contain a deck.");
        var sourceVersion = deck.FormatVersion;
        if (sourceVersion is < 1 or > Deck.CurrentFormatVersion) throw new InvalidDataException("Unsupported deck format version.");
        // Version 1 used Truth/Dare. Those values have no direct equivalent, so older
        // cards migrate conservatively to SFW and can be recategorized in the editor.
        if (sourceVersion == 1)
        {
            foreach (var card in deck.Cards ?? []) card.Category = CardCategory.Sfw;
        }
        if (sourceVersion < 4)
        {
            foreach (var card in deck.Cards ?? [])
            {
                if (string.IsNullOrWhiteSpace(card.Title)) card.Title = "Untitled Levemete";
                card.Activity = ActivityType.ActionSelf;
            }
            deck.FormatVersion = Deck.CurrentFormatVersion;
        }
        if (!preserveId) deck.Id = Guid.NewGuid();
        Validate(deck);
        return deck;
    }

    private static string NormalizeJsonPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Enter an export file path.");
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (!expanded.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) expanded += ".json";
        return Path.GetFullPath(expanded);
    }

    private static void Validate(Deck deck)
    {
        if (deck.FormatVersion is < 1 or > Deck.CurrentFormatVersion) throw new InvalidDataException("Unsupported deck format version.");
        deck.Name = deck.Name?.Trim() ?? string.Empty;
        if (deck.Name.Length is 0 or > MaxNameLength) throw new InvalidDataException($"Deck names must be 1-{MaxNameLength} characters.");
        deck.Cards ??= [];
        if (deck.Cards.Count > MaxCardsPerDeck) throw new InvalidDataException($"A deck may contain at most {MaxCardsPerDeck} cards.");
        var ids = new HashSet<Guid>();
        foreach (var card in deck.Cards)
        {
            card.Title = card.Title?.Trim() ?? string.Empty;
            if (card.Title.Length is 0 or > MaxTitleLength) throw new InvalidDataException($"Card titles must be 1-{MaxTitleLength} characters.");
            card.Text = card.Text?.Trim() ?? string.Empty;
            if (card.Text.Length is 0 or > MaxCardLength) throw new InvalidDataException($"Card text must be 1-{MaxCardLength} characters.");
            const CardCategory allCategories = CardCategory.Sfw | CardCategory.Mixed | CardCategory.Nsfw | CardCategory.NsfwPlus;
            if (card.Category == CardCategory.None || (card.Category & ~allCategories) != 0)
                throw new InvalidDataException("Every card needs at least one valid category.");
            if (card.Keyword is not null && !Enum.IsDefined(card.Keyword.Value)) throw new InvalidDataException("A card has an unknown keyword.");
            if (!Enum.IsDefined(card.Activity)) throw new InvalidDataException("A card has an unknown activity type.");
            if (card.Id == Guid.Empty || !ids.Add(card.Id)) card.Id = Guid.NewGuid();
        }
    }

    private static Deck CreateStarterDeck() => new() { Name = "My First Deck", Cards = [] };
}
