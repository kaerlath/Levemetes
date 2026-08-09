using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using TruthOrDare.Models;

namespace TruthOrDare.Services;

public sealed class DeckStore
{
    private const int MaxDecks = 100;
    private const int MaxCardsPerDeck = 5000;
    private const int MaxCustomArtwork = 200;
    private const int MaxNameLength = 80;
    private const int MaxAuthorLength = 80;
    private const int MaxCardLength = 1000;
    private const int MaxFlavorTextLength = 240;
    private const int MaxTitleLength = 100;
    private const long MaxSourceImageBytes = 25L * 1024 * 1024;
    private const long MaxBundleBytes = 100L * 1024 * 1024;
    private const int ArtworkWidth = 768;
    private const int ArtworkHeight = 512;
    private readonly string decksDirectory;
    private readonly string artworkRoot;
    private readonly Action<Exception, string, string>? logWarning;
    private readonly JsonSerializerOptions jsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public DeckStore(string configDirectory, Action<Exception, string, string>? logWarning = null)
    {
        decksDirectory = Path.Combine(configDirectory, "decks");
        artworkRoot = Path.Combine(decksDirectory, "artwork");
        this.logWarning = logWarning;
    }

    public string DecksDirectory => decksDirectory;

    public List<Deck> LoadAll()
    {
        Directory.CreateDirectory(decksDirectory);
        var decks = new List<Deck>();
        foreach (var file in Directory.EnumerateFiles(decksDirectory, "*.json").Take(MaxDecks))
        {
            try { decks.Add(ReadJson(File.ReadAllBytes(file), preserveId: true)); }
            catch (Exception ex) { logWarning?.Invoke(ex, "Could not load deck {File}", file); }
        }
        if (decks.Count == 0)
        {
            var starter = new Deck { Name = "My First Deck", Cards = [] };
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
        var art = DeckArtworkDirectory(deck.Id);
        if (Directory.Exists(art)) Directory.Delete(art, true);
    }

    public string GetArtworkPath(Deck deck, Guid artworkId) =>
        Path.Combine(DeckArtworkDirectory(deck.Id), $"{artworkId:N}.jpg");

    public CustomArtworkAsset AddCustomArtwork(Deck deck, string sourcePath)
    {
        if (deck.CustomArtwork.Count >= MaxCustomArtwork)
            throw new InvalidOperationException($"A deck may contain at most {MaxCustomArtwork} custom images.");
        var fullPath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("The selected image does not exist.", fullPath);
        if (new FileInfo(fullPath).Length > MaxSourceImageBytes) throw new InvalidDataException("Images must be smaller than 25 MB.");
        var bytes = NormalizeImage(File.ReadAllBytes(fullPath));
        var hash = Hash(bytes);
        var existing = deck.CustomArtwork.FirstOrDefault(asset => asset.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;
        var asset = new CustomArtworkAsset
        {
            Name = CleanArtworkName(Path.GetFileNameWithoutExtension(fullPath)),
            Sha256 = hash,
        };
        WriteArtwork(deck.Id, asset.Id, bytes);
        deck.CustomArtwork.Add(asset);
        Save(deck);
        return asset;
    }

    public void DeleteCustomArtwork(Deck deck, Guid artworkId)
    {
        if (deck.Cards.Any(card => card.CustomArtworkId == artworkId))
            throw new InvalidOperationException("This artwork is used by one or more cards. Change those cards first.");
        deck.CustomArtwork.RemoveAll(asset => asset.Id == artworkId);
        var path = GetArtworkPath(deck, artworkId);
        if (File.Exists(path)) File.Delete(path);
        Save(deck);
    }

    public string Export(Deck deck, string requestedPath)
    {
        var path = NormalizeBundlePath(requestedPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Choose an export directory."));
        var temporary = path + ".tmp";
        if (File.Exists(temporary)) File.Delete(temporary);
        File.WriteAllBytes(temporary, ExportBundleBytes(deck));
        File.Move(temporary, path, true);
        return path;
    }

    public byte[] ExportBundleBytes(Deck deck)
    {
        Validate(deck, requireArtworkFiles: true);
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var deckEntry = archive.CreateEntry("deck.json", CompressionLevel.Optimal);
            using (var stream = deckEntry.Open()) JsonSerializer.Serialize(stream, deck, jsonOptions);
            foreach (var asset in deck.CustomArtwork)
            {
                var entry = archive.CreateEntry($"images/{asset.Id:N}.jpg", CompressionLevel.Optimal);
                using var input = File.OpenRead(GetArtworkPath(deck, asset.Id));
                using var output = entry.Open();
                input.CopyTo(output);
            }
        }
        if (memory.Length > MaxBundleBytes) throw new InvalidDataException("The deck bundle is larger than 100 MB.");
        return memory.ToArray();
    }

    public Deck ImportBundleBytes(byte[] bundle)
    {
        if (bundle.Length is 0 || bundle.LongLength > MaxBundleBytes)
            throw new InvalidDataException("The received deck bundle is empty or larger than 100 MB.");
        Directory.CreateDirectory(decksDirectory);
        var temporary = Path.Combine(decksDirectory, $"network-{Guid.NewGuid():N}.levemetesdeck");
        try
        {
            File.WriteAllBytes(temporary, bundle);
            return Import(temporary);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public Deck Import(string path)
    {
        var imported = ReadImport(path);
        var deck = imported.Deck;
        deck.Id = Guid.NewGuid();
        InstallArtwork(deck, imported.Images);
        Save(deck);
        return deck;
    }

    public (int Added, int Skipped) Merge(string path, Deck destination)
    {
        var imported = ReadImport(path);
        var artMap = MergeArtwork(imported, destination);
        foreach (var card in imported.Deck.Cards)
            if (card.CustomArtworkId is Guid sourceId) card.CustomArtworkId = artMap[sourceId];

        var existing = new HashSet<string>(destination.Cards.Select(card => CardFingerprint(card, destination)), StringComparer.Ordinal);
        var additions = new List<Card>();
        var skipped = 0;
        foreach (var card in imported.Deck.Cards)
        {
            if (!existing.Add(CardFingerprint(card, destination))) { skipped++; continue; }
            card.Id = Guid.NewGuid();
            additions.Add(card);
        }
        destination.Cards.AddRange(additions);
        Save(destination);
        return (additions.Count, skipped);
    }

    private ImportedDeck ReadImport(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Choose a Levemetes deck file.");
        var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
        if (!File.Exists(fullPath)) throw new FileNotFoundException("The selected deck file does not exist.", fullPath);
        var info = new FileInfo(fullPath);
        if (info.Length > MaxBundleBytes) throw new InvalidDataException("Deck bundles must be smaller than 100 MB.");
        if (fullPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return new ImportedDeck(ReadJson(File.ReadAllBytes(fullPath), preserveId: false), []);
        if (!fullPath.EndsWith(".levemetesdeck", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose a .levemetesdeck bundle or legacy .json deck.");

        using var archive = ZipFile.OpenRead(fullPath);
        if (archive.Entries.Count > MaxCustomArtwork + 1) throw new InvalidDataException("The deck bundle contains too many files.");
        var deckEntries = archive.Entries.Where(entry => entry.FullName.Equals("deck.json", StringComparison.OrdinalIgnoreCase)).ToList();
        if (deckEntries.Count != 1 || deckEntries[0].Length > 5 * 1024 * 1024)
            throw new InvalidDataException("The bundle must contain one valid deck.json file.");
        byte[] json;
        using (var stream = deckEntries[0].Open()) { using var memory = new MemoryStream(); stream.CopyTo(memory); json = memory.ToArray(); }
        var deck = ReadJson(json, preserveId: false);
        var images = new Dictionary<Guid, byte[]>();
        foreach (var asset in deck.CustomArtwork)
        {
            var expected = $"images/{asset.Id:N}.jpg";
            var entries = archive.Entries.Where(entry => entry.FullName.Equals(expected, StringComparison.OrdinalIgnoreCase)).ToList();
            if (entries.Count != 1 || entries[0].Length > MaxSourceImageBytes)
                throw new InvalidDataException($"Custom artwork ‘{asset.Name}’ is missing or invalid.");
            using var stream = entries[0].Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var normalized = memory.ToArray();
            ValidateStoredArtwork(normalized);
            if (!Hash(normalized).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Custom artwork ‘{asset.Name}’ did not pass its integrity check.");
            images[asset.Id] = normalized;
        }
        return new ImportedDeck(deck, images);
    }

    private Dictionary<Guid, Guid> MergeArtwork(ImportedDeck imported, Deck destination)
    {
        var map = new Dictionary<Guid, Guid>();
        foreach (var source in imported.Deck.CustomArtwork)
        {
            var existing = destination.CustomArtwork.FirstOrDefault(asset => asset.Sha256.Equals(source.Sha256, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) { map[source.Id] = existing.Id; continue; }
            if (!imported.Images.TryGetValue(source.Id, out var bytes))
                throw new InvalidDataException($"Custom artwork ‘{source.Name}’ is missing from the imported deck.");
            var added = new CustomArtworkAsset { Name = source.Name, Sha256 = source.Sha256 };
            WriteArtwork(destination.Id, added.Id, bytes);
            destination.CustomArtwork.Add(added);
            map[source.Id] = added.Id;
        }
        return map;
    }

    private void InstallArtwork(Deck deck, IReadOnlyDictionary<Guid, byte[]> images)
    {
        var oldToNew = new Dictionary<Guid, Guid>();
        foreach (var asset in deck.CustomArtwork)
        {
            var old = asset.Id;
            if (!images.TryGetValue(old, out var bytes))
                throw new InvalidDataException($"Custom artwork ‘{asset.Name}’ is not included in this deck file.");
            asset.Id = Guid.NewGuid();
            oldToNew[old] = asset.Id;
            WriteArtwork(deck.Id, asset.Id, bytes);
        }
        foreach (var card in deck.Cards)
            if (card.CustomArtworkId is Guid old) card.CustomArtworkId = oldToNew[old];
    }

    private Deck ReadJson(byte[] json, bool preserveId)
    {
        if (json.Length > 5 * 1024 * 1024) throw new InvalidDataException("Deck data must be smaller than 5 MB.");
        var deck = JsonSerializer.Deserialize<Deck>(json, jsonOptions) ?? throw new InvalidDataException("The file does not contain a deck.");
        var sourceVersion = deck.FormatVersion;
        if (sourceVersion is < 1 or > Deck.CurrentFormatVersion) throw new InvalidDataException("Unsupported deck format version.");
        if (sourceVersion == 1) foreach (var card in deck.Cards ?? []) card.Category = CardCategory.Sfw;
        if (sourceVersion < 4) foreach (var card in deck.Cards ?? []) { if (string.IsNullOrWhiteSpace(card.Title)) card.Title = "Untitled Levemete"; card.Activity = ActivityType.ActionSelf; }
        if (sourceVersion < 5) foreach (var card in deck.Cards ?? []) card.FlavorText ??= string.Empty;
        if (sourceVersion < 6) foreach (var card in deck.Cards ?? []) card.Artwork = card.Activity switch
        {
            ActivityType.ActionOtherVolunteer => ArtworkChoice.SfwFiresideFellowship,
            ActivityType.ActionChoice => ArtworkChoice.SfwScholarsReflection,
            ActivityType.ActionRandom => ArtworkChoice.SfwStarlitJourney,
            ActivityType.RevelationThought => ArtworkChoice.SfwFestivalSpirit,
            ActivityType.RevelationExperience => ArtworkChoice.SfwGardenSanctuary,
            _ => ArtworkChoice.SfwAdventurersResolve,
        };
        deck.CustomArtwork ??= [];
        deck.FormatVersion = Deck.CurrentFormatVersion;
        if (!preserveId) deck.Id = Guid.NewGuid();
        Validate(deck, requireArtworkFiles: preserveId);
        return deck;
    }

    private void Validate(Deck deck, bool requireArtworkFiles = false)
    {
        if (deck.FormatVersion is < 1 or > Deck.CurrentFormatVersion) throw new InvalidDataException("Unsupported deck format version.");
        deck.Name = deck.Name?.Trim() ?? string.Empty;
        if (deck.Name.Length is 0 or > MaxNameLength) throw new InvalidDataException($"Deck names must be 1-{MaxNameLength} characters.");
        deck.Author = deck.Author?.Trim() ?? string.Empty;
        if (deck.Author.Length > MaxAuthorLength) throw new InvalidDataException($"Deck authors may contain at most {MaxAuthorLength} characters.");
        deck.Cards ??= [];
        deck.CustomArtwork ??= [];
        if (deck.Cards.Count > MaxCardsPerDeck) throw new InvalidDataException($"A deck may contain at most {MaxCardsPerDeck} cards.");
        if (deck.CustomArtwork.Count > MaxCustomArtwork) throw new InvalidDataException($"A deck may contain at most {MaxCustomArtwork} custom images.");
        var artworkIds = new HashSet<Guid>();
        foreach (var asset in deck.CustomArtwork)
        {
            asset.Name = CleanArtworkName(asset.Name);
            if (asset.Id == Guid.Empty || !artworkIds.Add(asset.Id)) throw new InvalidDataException("Custom artwork identifiers must be unique.");
            if (asset.Sha256.Length != 64 || asset.Sha256.Any(ch => !Uri.IsHexDigit(ch))) throw new InvalidDataException("Custom artwork has an invalid integrity value.");
            if (requireArtworkFiles && !File.Exists(GetArtworkPath(deck, asset.Id))) throw new InvalidDataException($"Custom artwork ‘{asset.Name}’ is missing from local storage.");
        }
        var ids = new HashSet<Guid>();
        foreach (var card in deck.Cards)
        {
            card.Title = card.Title?.Trim() ?? string.Empty;
            if (card.Title.Length is 0 or > MaxTitleLength) throw new InvalidDataException($"Card titles must be 1-{MaxTitleLength} characters.");
            card.Text = card.Text?.Trim() ?? string.Empty;
            if (card.Text.Length is 0 or > MaxCardLength || StripFormatting(card.Text).Length == 0) throw new InvalidDataException($"Card text must be 1-{MaxCardLength} characters.");
            card.FlavorText = card.FlavorText?.Trim() ?? string.Empty;
            if (card.FlavorText.Length > MaxFlavorTextLength) throw new InvalidDataException($"Flavor text may contain at most {MaxFlavorTextLength} characters.");
            const CardCategory all = CardCategory.Sfw | CardCategory.Mixed | CardCategory.Nsfw | CardCategory.NsfwPlus;
            if (card.Category == CardCategory.None || (card.Category & ~all) != 0) throw new InvalidDataException("Every card needs at least one valid category.");
            if (card.Keyword is not null && !Enum.IsDefined(card.Keyword.Value)) throw new InvalidDataException("A card has an unknown keyword.");
            if (!Enum.IsDefined(card.Activity) || !Enum.IsDefined(card.Artwork)) throw new InvalidDataException("A card has an unknown activity or artwork choice.");
            if (card.CustomArtworkId is Guid custom && !artworkIds.Contains(custom)) throw new InvalidDataException("A card refers to missing custom artwork.");
            if (card.Id == Guid.Empty || !ids.Add(card.Id)) card.Id = Guid.NewGuid();
        }
    }

    private string CardFingerprint(Card card, Deck deck)
    {
        var art = card.CustomArtworkId is Guid id ? deck.CustomArtwork.First(asset => asset.Id == id).Sha256 : ((int)card.Artwork).ToString();
        return string.Join('\u001f', Normalize(card.Title), ((int)card.Activity).ToString(), art, ((int)card.Category).ToString(),
            card.Keyword?.ToString() ?? string.Empty, Normalize(card.Text), Normalize(card.FlavorText));
    }

    private static byte[] NormalizeImage(byte[] source)
    {
        try
        {
            using var input = new MemoryStream(source);
            using var image = Image.FromStream(input, useEmbeddedColorManagement: true, validateImageData: true);
            if (image.Width < 64 || image.Height < 64 || image.Width > 16000 || image.Height > 16000)
                throw new InvalidDataException("Images must be between 64 and 16,000 pixels in each dimension.");
            using var outputImage = new Bitmap(ArtworkWidth, ArtworkHeight, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(outputImage))
            {
                graphics.Clear(Color.Black);
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                var scale = Math.Max((double)ArtworkWidth / image.Width, (double)ArtworkHeight / image.Height);
                var width = image.Width * scale;
                var height = image.Height * scale;
                graphics.DrawImage(image, (float)((ArtworkWidth - width) / 2), (float)((ArtworkHeight - height) / 2), (float)width, (float)height);
            }
            using var output = new MemoryStream();
            var codec = ImageCodecInfo.GetImageEncoders().First(item => item.FormatID == ImageFormat.Jpeg.Guid);
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, 88L);
            outputImage.Save(output, codec, parameters);
            return output.ToArray();
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or OutOfMemoryException)
        {
            throw new InvalidDataException("The selected file is not a readable PNG, JPEG, BMP, or GIF image.", ex);
        }
    }

    private static void ValidateStoredArtwork(byte[] source)
    {
        try
        {
            using var input = new MemoryStream(source);
            using var image = Image.FromStream(input, useEmbeddedColorManagement: true, validateImageData: true);
            if (image.Width != ArtworkWidth || image.Height != ArtworkHeight || image.RawFormat.Guid != ImageFormat.Jpeg.Guid)
                throw new InvalidDataException($"Bundled custom artwork must be a {ArtworkWidth}×{ArtworkHeight} JPEG image.");
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or OutOfMemoryException)
        {
            throw new InvalidDataException("Bundled custom artwork is not a readable JPEG image.", ex);
        }
    }

    private void WriteArtwork(Guid deckId, Guid artId, byte[] bytes)
    {
        var directory = DeckArtworkDirectory(deckId);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, $"{artId:N}.jpg"), bytes);
    }

    private string DeckArtworkDirectory(Guid deckId) => Path.Combine(artworkRoot, deckId.ToString("N"));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string Normalize(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
    private static string CleanArtworkName(string value)
    {
        var name = string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (name.Length == 0) name = "Custom Artwork";
        return name[..Math.Min(name.Length, 80)];
    }
    private static string NormalizeBundlePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Enter an export file path.");
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (!expanded.EndsWith(".levemetesdeck", StringComparison.OrdinalIgnoreCase)) expanded += ".levemetesdeck";
        return Path.GetFullPath(expanded);
    }
    private static string StripFormatting(string text)
    {
        foreach (var tag in new[] { "b", "i", "u", "c" })
        {
            text = text.Replace($"[{tag}]", string.Empty, StringComparison.OrdinalIgnoreCase);
            text = text.Replace($"[/{tag}]", string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        return text.Trim();
    }
    private sealed record ImportedDeck(Deck Deck, Dictionary<Guid, byte[]> Images);
}
