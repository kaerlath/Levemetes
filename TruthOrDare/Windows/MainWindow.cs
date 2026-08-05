using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using TruthOrDare.Models;
using TruthOrDare.Services;

namespace TruthOrDare.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private const int MaxCardText = 1000;
    private const int MaxCardTitle = 100;
    private const int MaxDeckName = 80;
    private static readonly CardCategory[] BasicCategories =
        [CardCategory.Sfw, CardCategory.Mixed, CardCategory.Nsfw, CardCategory.NsfwPlus];
    private readonly Configuration configuration;
    private readonly DeckStore store;
    private readonly Action<Configuration> saveConfiguration;
    private readonly string cardBackPath;
    private readonly string templateDirectory;
    private readonly GameSession session = new();
    private readonly List<Deck> decks;
    private Deck selectedDeck;
    private string status = string.Empty;
    private bool statusIsError;
    private string newDeckName = string.Empty;
    private string cardText = string.Empty;
    private string cardTitle = string.Empty;
    private CardCategory cardCategory;
    private ActivityType activityType;
    private CardCategory playCategory;
    private CardKeyword? cardKeyword;
    private Guid? editingCardId;
    private Guid? pendingDeleteCardId;
    private bool requestDeleteDeck;
    private string transferPath = string.Empty;

    public MainWindow(Configuration configuration, DeckStore store, Action<Configuration> saveConfiguration,
        string cardBackPath, string templateDirectory)
        : base("Levemetes##LevemetesMain")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(500, 470),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        this.configuration = configuration;
        this.store = store;
        this.saveConfiguration = saveConfiguration;
        this.cardBackPath = cardBackPath;
        this.templateDirectory = templateDirectory;
        playCategory = configuration.SelectedCategory;
        if (!BasicCategories.Contains(playCategory))
        {
            playCategory = CardCategory.Sfw;
            configuration.SelectedCategory = playCategory;
        }
        decks = store.LoadAll();
        selectedDeck = decks.FirstOrDefault(deck => deck.Id == configuration.SelectedDeckId) ?? decks[0];
        SelectDeck(selectedDeck);
    }

    public void Dispose() { }

    public override void Draw()
    {
        DrawDeckSelector();
        ImGui.Separator();
        if (ImGui.BeginTabBar("MainTabs"))
        {
            if (ImGui.BeginTabItem("Play")) { DrawPlayTab(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Cards")) { DrawCardsTab(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Decks & Sharing")) { DrawDecksTab(); ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }
        DrawStatus();
        DrawConfirmations();
    }

    private void DrawDeckSelector()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Deck");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(260 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("##Deck", selectedDeck.Name))
        {
            foreach (var deck in decks)
            {
                if (ImGui.Selectable($"{deck.Name} ({deck.Cards.Count})##{deck.Id}", deck.Id == selectedDeck.Id)) SelectDeck(deck);
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.TextDisabled($"{session.Remaining} remaining");
    }

    private void DrawPlayTab()
    {
        ImGui.Spacing();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Intensity (Heat) Category");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(180 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("##PlayCategory", CategoryLabel(playCategory)))
        {
            foreach (var category in BasicCategories)
            {
                if (ImGui.Selectable(CategoryLabel(category), playCategory == category))
                {
                    playCategory = category;
                    configuration.SelectedCategory = category;
                    saveConfiguration(configuration);
                    session.Reset(selectedDeck, playCategory);
                    SetStatus($"Ready to draw {CategoryLabel(playCategory)} cards.");
                }
            }
            ImGui.EndCombo();
        }
        var categoryCount = selectedDeck.Cards.Count(card => card.Category.HasFlag(playCategory));
        ImGui.SameLine();
        ImGui.TextDisabled($"{categoryCount} cards");
        ImGui.Dummy(new Vector2(0, ImGui.GetTextLineHeightWithSpacing() * 2));
        var width = MathF.Min(ImGui.GetContentRegionAvail().X, 360 * ImGuiHelpers.GlobalScale);
        var cardSize = new Vector2(width, width * 1.50f);
        ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX(), (ImGui.GetContentRegionMax().X - width) / 2));
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 0);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 12 * ImGuiHelpers.GlobalScale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.72f, 0.59f, 0.27f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.08f, 0.07f, 0.12f, 0.98f));
        if (ImGui.BeginChild("CardFace", cardSize, true))
        {
            if (session.CurrentCard is null)
            {
                var texture = Plugin.TextureProvider.GetFromFile(cardBackPath).GetWrapOrDefault();
                if (texture is not null)
                {
                    var available = ImGui.GetContentRegionAvail();
                    var scale = MathF.Min(available.X / texture.Size.X, available.Y / texture.Size.Y);
                    var imageSize = texture.Size * scale;
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, (available.X - imageSize.X) / 2));
                    ImGui.Image(texture.Handle, imageSize);
                }
                else CenteredText("LEVEMETES", new Vector4(0.82f, 0.72f, 0.39f, 1f));
            }
            else
            {
                DrawRevealedTemplate(session.CurrentCard, cardSize);
            }
        }
        ImGui.EndChild();
        if (session.CurrentCard is null)
            DrawSegmentedBorder(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), [new Vector4(0.72f, 0.59f, 0.27f, 1f)]);
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(3);

        ImGui.Dummy(new Vector2(0, ImGui.GetTextLineHeightWithSpacing()));
        var copyButtonSize = new Vector2(190, 34) * ImGuiHelpers.GlobalScale;
        ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX(),
            ImGui.GetCursorPosX() + (ImGui.GetContentRegionAvail().X - copyButtonSize.X) / 2));
        if (session.CurrentCard is null) ImGui.BeginDisabled();
        if (ImGui.Button("Copy Text of Card", copyButtonSize) && session.CurrentCard is Card cardToCopy)
        {
            ImGui.SetClipboardText(cardToCopy.Text);
            SetStatus("Card text copied to the clipboard.");
        }
        if (session.CurrentCard is null) ImGui.EndDisabled();

        ImGui.Dummy(new Vector2(0, ImGui.GetTextLineHeightWithSpacing()));
        var canDraw = categoryCount > 0 && session.Remaining > 0;
        var buttonGap = 12 * ImGuiHelpers.GlobalScale;
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var actionButtonWidth = MathF.Min(210 * ImGuiHelpers.GlobalScale, (availableWidth - buttonGap) / 2);
        var actionButtonSize = new Vector2(actionButtonWidth, 44 * ImGuiHelpers.GlobalScale);
        var actionGroupWidth = actionButtonSize.X * 2 + buttonGap;
        ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX(),
            ImGui.GetCursorPosX() + (availableWidth - actionGroupWidth) / 2));
        if (!canDraw) ImGui.BeginDisabled();
        if (ImGui.Button("Draw", actionButtonSize)) session.Draw(selectedDeck, playCategory);
        if (!canDraw) ImGui.EndDisabled();
        ImGui.SameLine(0, buttonGap);
        if (ImGui.Button("Shuffle / Reset", actionButtonSize)) { session.Reset(selectedDeck, playCategory); SetStatus("Draw pile shuffled."); }
        if (categoryCount > 0 && session.Remaining == 0) ImGui.TextDisabled("No cards remain in this category. Shuffle / Reset to play again.");
    }

    private void DrawCardsTab()
    {
        ImGui.Spacing();
        ImGui.TextUnformatted(editingCardId is null ? "Add a card" : "Edit card");
        ImGui.InputTextWithHint("##CardTitle", "Levemete title", ref cardTitle, MaxCardTitle + 1);
        ImGui.TextUnformatted("Activity type");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(250 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("##ActivityType", ActivityLabel(activityType)))
        {
            foreach (var activity in Enum.GetValues<ActivityType>())
                if (ImGui.Selectable(ActivityLabel(activity), activityType == activity)) activityType = activity;
            ImGui.EndCombo();
        }
        ImGui.Separator();
        ImGui.TextUnformatted("Categories (select one or more)");
        foreach (var category in BasicCategories)
        {
            if (category != CardCategory.Sfw) ImGui.SameLine();
            var selected = cardCategory.HasFlag(category);
            if (ImGui.Checkbox(CategoryLabel(category), ref selected))
            {
                if (selected) cardCategory |= category;
                else cardCategory &= ~category;
            }
        }
        ImGui.Separator();
        ImGui.TextUnformatted("Optional keyword");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(190 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("##Keyword", cardKeyword is null ? "None" : KeywordLabel(cardKeyword.Value)))
        {
            if (ImGui.Selectable("None", cardKeyword is null)) cardKeyword = null;
            foreach (var keyword in Enum.GetValues<CardKeyword>())
                if (ImGui.Selectable(KeywordLabel(keyword), cardKeyword == keyword)) cardKeyword = keyword;
            ImGui.EndCombo();
        }
        ImGui.InputTextMultiline("##CardText", ref cardText, MaxCardText + 1, new Vector2(-1, 85 * ImGuiHelpers.GlobalScale));
        var valid = cardCategory != CardCategory.None && !string.IsNullOrWhiteSpace(cardTitle) && cardTitle.Trim().Length <= MaxCardTitle
            && !string.IsNullOrWhiteSpace(cardText) && cardText.Trim().Length <= MaxCardText;
        if (!valid) ImGui.BeginDisabled();
        if (ImGui.Button(editingCardId is null ? "Add Card" : "Save Changes")) SaveCard();
        if (!valid) ImGui.EndDisabled();
        if (editingCardId is not null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) ClearEditor();
        }
        ImGui.Separator();
        if (ImGui.BeginChild("CardList", Vector2.Zero, false))
        {
            foreach (var card in selectedDeck.Cards.ToList())
            {
                ImGui.TextUnformatted(card.Title);
                ImGui.SameLine();
                ImGui.TextDisabled(ActivityLabel(card.Activity));
                DrawCategoryLabelsInline(card.Category);
                if (card.Keyword is CardKeyword keyword)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(0.82f, 0.72f, 0.39f, 1f), KeywordLabel(keyword));
                }
                ImGui.SameLine();
                ImGui.PushTextWrapPos(ImGui.GetWindowWidth() - 140 * ImGuiHelpers.GlobalScale);
                ImGui.TextWrapped(card.Text);
                ImGui.PopTextWrapPos();
                ImGui.PushID(card.Id.ToString());
                if (ImGui.SmallButton("Edit")) BeginEdit(card);
                ImGui.SameLine();
                if (ImGui.SmallButton("Delete")) pendingDeleteCardId = card.Id;
                ImGui.PopID();
                ImGui.Separator();
            }
        }
        ImGui.EndChild();
    }

    private void DrawDecksTab()
    {
        ImGui.Spacing();
        ImGui.TextUnformatted("Create deck");
        ImGui.SetNextItemWidth(300 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##NewDeck", "Deck name", ref newDeckName, MaxDeckName + 1);
        ImGui.SameLine();
        if (ImGui.Button("Create") && !string.IsNullOrWhiteSpace(newDeckName)) CreateDeck();
        ImGui.Separator();
        ImGui.TextUnformatted("Selected deck");
        var editedName = selectedDeck.Name;
        ImGui.SetNextItemWidth(300 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("Name", ref editedName, MaxDeckName + 1) && !string.IsNullOrWhiteSpace(editedName)) selectedDeck.Name = editedName;
        ImGui.SameLine();
        if (ImGui.Button("Save Name")) SaveDeck("Deck name saved.");
        ImGui.SameLine();
        if (decks.Count <= 1) ImGui.BeginDisabled();
        if (ImGui.Button("Delete Deck")) requestDeleteDeck = true;
        if (decks.Count <= 1) ImGui.EndDisabled();
        ImGui.Separator();
        ImGui.TextWrapped("Import or export a portable JSON deck. Paths may be absolute or relative to the game process. Existing export files are replaced.");
        ImGui.InputText("JSON path", ref transferPath, 1024);
        if (ImGui.Button("Export Selected")) TryAction(() => SetStatus($"Exported to {store.Export(selectedDeck, transferPath)}"));
        ImGui.SameLine();
        if (ImGui.Button("Import as New Deck")) TryAction(ImportDeck);
        ImGui.TextDisabled($"Your live deck files are stored in: {store.DecksDirectory}");
    }

    private void DrawStatus()
    {
        if (string.IsNullOrWhiteSpace(status)) return;
        ImGui.Separator();
        ImGui.TextColored(statusIsError ? new Vector4(1f, .35f, .35f, 1f) : new Vector4(.45f, .9f, .55f, 1f), status);
    }

    private void DrawConfirmations()
    {
        if (pendingDeleteCardId is not null) ImGui.OpenPopup("Delete card?");
        if (requestDeleteDeck) ImGui.OpenPopup("Delete deck?");
        if (ImGui.BeginPopupModal("Delete card?", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted("Delete this card? This cannot be undone.");
            if (ImGui.Button("Delete"))
            {
                selectedDeck.Cards.RemoveAll(card => card.Id == pendingDeleteCardId);
                SaveDeck("Card deleted.");
                ClearEditor();
                pendingDeleteCardId = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) { pendingDeleteCardId = null; ImGui.CloseCurrentPopup(); }
            ImGui.EndPopup();
        }
        if (ImGui.BeginPopupModal("Delete deck?", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted($"Delete “{selectedDeck.Name}” and all of its cards?");
            if (ImGui.Button("Delete"))
            {
                var deleted = selectedDeck;
                selectedDeck = decks.First(deck => deck.Id != deleted.Id);
                decks.Remove(deleted);
                TryAction(() => store.Delete(deleted));
                SelectDeck(selectedDeck);
                requestDeleteDeck = false;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) { requestDeleteDeck = false; ImGui.CloseCurrentPopup(); }
            ImGui.EndPopup();
        }
    }

    private void SaveCard()
    {
        var text = cardText.Trim();
        if (editingCardId is Guid id)
        {
            var card = selectedDeck.Cards.FirstOrDefault(item => item.Id == id);
            if (card is null) { SetStatus("That card no longer exists.", true); ClearEditor(); return; }
            card.Title = cardTitle.Trim();
            card.Activity = activityType;
            card.Category = cardCategory;
            card.Keyword = cardKeyword;
            card.Text = text;
        }
        else selectedDeck.Cards.Add(new Card
        {
            Title = cardTitle.Trim(), Activity = activityType, Category = cardCategory, Keyword = cardKeyword, Text = text,
        });
        SaveDeck(editingCardId is null ? "Card added." : "Card updated.");
        ClearEditor();
    }

    private void BeginEdit(Card card)
    {
        editingCardId = card.Id;
        cardTitle = card.Title;
        activityType = card.Activity;
        cardCategory = card.Category;
        cardKeyword = card.Keyword;
        cardText = card.Text;
    }

    private void ClearEditor()
    {
        editingCardId = null;
        cardTitle = string.Empty;
        activityType = ActivityType.ActionSelf;
        cardCategory = CardCategory.Sfw;
        cardKeyword = null;
        cardText = string.Empty;
    }

    private void SaveDeck(string message) => TryAction(() =>
    {
        store.Save(selectedDeck);
        session.Reset(selectedDeck, playCategory);
        SetStatus(message);
    });

    private void CreateDeck()
    {
        var deck = new Deck { Name = newDeckName.Trim() };
        TryAction(() =>
        {
            store.Save(deck);
            decks.Add(deck);
            newDeckName = string.Empty;
            SelectDeck(deck);
            SetStatus("Deck created.");
        });
    }

    private void ImportDeck()
    {
        var deck = store.Import(transferPath);
        decks.Add(deck);
        SelectDeck(deck);
        SetStatus($"Imported “{deck.Name}”.");
    }

    private void SelectDeck(Deck deck)
    {
        selectedDeck = deck;
        configuration.SelectedDeckId = deck.Id;
        saveConfiguration(configuration);
        session.Reset(deck, playCategory);
        ClearEditor();
        transferPath = Path.Combine(store.DecksDirectory, "exports", SanitizeFileName(deck.Name) + ".json");
    }

    private void TryAction(Action action)
    {
        try { action(); }
        catch (Exception ex) { Plugin.Log.Error(ex, "Levemetes operation failed"); SetStatus(ex.Message, true); }
    }

    private void SetStatus(string message, bool error = false) { status = message; statusIsError = error; }

    private static void CenteredText(string text, Vector4 color)
    {
        var width = ImGui.CalcTextSize(text).X;
        ImGui.SetCursorPosX(MathF.Max(0, (ImGui.GetWindowWidth() - width) / 2));
        ImGui.TextColored(color, text);
    }

    private void DrawRevealedTemplate(Card card, Vector2 cardSize)
    {
        var path = Path.Combine(templateDirectory, TemplateFileName(card.Activity));
        var texture = Plugin.TextureProvider.GetFromFile(path).GetWrapOrDefault();
        if (texture is null)
        {
            CenteredText("Card template not found", new Vector4(1f, .35f, .35f, 1f));
            return;
        }

        ImGui.SetCursorPos(Vector2.Zero);
        ImGui.Image(texture.Handle, cardSize);

        DrawCenteredOverlayText(card.Title, cardSize.Y * 0.026f, 8f,
            new Vector4(0.19f, 0.12f, 0.07f, 1f));
        DrawCenteredOverlayText(ActivityLabel(card.Activity), cardSize.Y * 0.074f, 1f,
            new Vector4(0.28f, 0.20f, 0.10f, 1f));

        ImGui.SetCursorPos(new Vector2(0, cardSize.Y * 0.395f));
        DrawCategoryHeading(card.Category);
        if (card.Keyword is CardKeyword keyword)
            CenteredText(KeywordLabel(keyword), new Vector4(0.45f, 0.30f, 0.10f, 1f));

        ImGui.SetCursorPos(new Vector2(cardSize.X * 0.09f, cardSize.Y * 0.49f));
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.18f, 0.12f, 0.08f, 1f));
        var labelSize = ImGui.GetFontSize();
        ImGui.SetWindowFontScale((labelSize + 2f * ImGuiHelpers.GlobalScale) / labelSize);
        ImGui.TextUnformatted("Objective");
        ImGui.SetWindowFontScale(1f);
        ImGui.PopStyleColor();

        ImGui.SetCursorPos(new Vector2(cardSize.X * 0.09f, cardSize.Y * 0.55f));
        ImGui.PushTextWrapPos(cardSize.X * 0.91f);
        ImGui.PushStyleColor(ImGuiCol.Text, CardInkColor(card.Category));
        var bodySize = ImGui.GetFontSize();
        ImGui.SetWindowFontScale((bodySize + 2f * ImGuiHelpers.GlobalScale) / bodySize);
        ImGui.TextWrapped(card.Text);
        ImGui.SetWindowFontScale(1f);
        ImGui.PopStyleColor();
        ImGui.PopTextWrapPos();
    }

    private static void DrawCenteredOverlayText(string text, float y, float extraPoints, Vector4 color)
    {
        ImGui.SetCursorPosX(0);
        ImGui.SetCursorPosY(y);
        var originalSize = ImGui.GetFontSize();
        ImGui.SetWindowFontScale((originalSize + extraPoints * ImGuiHelpers.GlobalScale) / originalSize);
        CenteredText(text, color);
        ImGui.SetWindowFontScale(1f);
    }

    private static string TemplateFileName(ActivityType activity) => activity switch
    {
        ActivityType.ActionSelf => "action-self.png",
        ActivityType.ActionOtherVolunteer => "action-other-volunteer.png",
        ActivityType.ActionChoice => "action-choice.png",
        ActivityType.ActionRandom => "action-random.png",
        ActivityType.RevelationThought => "revelation-thought.png",
        ActivityType.RevelationExperience => "revelation-experience.png",
        _ => "action-self.png",
    };

    private static string ActivityLabel(ActivityType activity) => activity switch
    {
        ActivityType.ActionSelf => "Action (Self)",
        ActivityType.ActionOtherVolunteer => "Action (Other-Volunteer)",
        ActivityType.ActionChoice => "Action (Choice)",
        ActivityType.ActionRandom => "Action (Random)",
        ActivityType.RevelationThought => "Revelation (Thought)",
        ActivityType.RevelationExperience => "Revelation (Experience)",
        _ => activity.ToString(),
    };

    private static string CategoryLabel(CardCategory category)
    {
        var labels = CategoriesIn(category).Select(SingleCategoryLabel).ToArray();
        return labels.Length == 0 ? "None" : string.Join(" + ", labels);
    }

    private static string SingleCategoryLabel(CardCategory category) => category switch
    {
        CardCategory.Sfw => "SFW",
        CardCategory.Mixed => "MIXED",
        CardCategory.Nsfw => "NSFW",
        CardCategory.NsfwPlus => "NSFW+",
        _ => category.ToString(),
    };

    private static Vector4 CategoryColor(CardCategory category) => category switch
    {
        CardCategory.Sfw => new Vector4(0.08f, 0.40f, 0.14f, 1f),
        CardCategory.Mixed => new Vector4(0.10f, 0.26f, 0.55f, 1f),
        CardCategory.Nsfw => new Vector4(1f, 0.46f, 0.46f, 1f),
        CardCategory.NsfwPlus => new Vector4(0.58f, 0.08f, 0.10f, 1f),
        _ => Vector4.One,
    };

    private static Vector4 CardInkColor(CardCategory categories)
    {
        var values = CategoriesIn(categories).ToArray();
        if (values.Length != 1) return new Vector4(0.20f, 0.13f, 0.08f, 1f);
        return values[0] switch
        {
            CardCategory.Sfw => new Vector4(0.05f, 0.29f, 0.09f, 1f),
            CardCategory.Mixed => new Vector4(0.06f, 0.18f, 0.43f, 1f),
            CardCategory.Nsfw => new Vector4(0.58f, 0.13f, 0.13f, 1f),
            CardCategory.NsfwPlus => new Vector4(0.34f, 0.03f, 0.04f, 1f),
            _ => new Vector4(0.20f, 0.13f, 0.08f, 1f),
        };
    }

    private static IEnumerable<CardCategory> CategoriesIn(CardCategory categories) =>
        BasicCategories.Where(category => categories.HasFlag(category));

    private static void DrawCategoryHeading(CardCategory categories)
    {
        var values = CategoriesIn(categories).ToArray();
        var originalSize = ImGui.GetFontSize();
        var headingScale = (originalSize + 5f * ImGuiHelpers.GlobalScale) / originalSize;
        ImGui.SetWindowFontScale(headingScale);
        var gap = 7 * ImGuiHelpers.GlobalScale;
        var totalWidth = values.Sum(value => CategoryBadgeSize(value).X) + MathF.Max(0, values.Length - 1) * gap;
        ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX(), (ImGui.GetWindowWidth() - totalWidth) / 2));
        for (var index = 0; index < values.Length; index++)
        {
            if (index > 0) ImGui.SameLine(0, gap);
            DrawCategoryBadge(values[index]);
        }
        ImGui.SetWindowFontScale(1f);
    }

    private static void DrawCategoryLabelsInline(CardCategory categories)
    {
        var values = CategoriesIn(categories).ToArray();
        for (var index = 0; index < values.Length; index++)
        {
            if (index > 0) ImGui.SameLine(0, 5 * ImGuiHelpers.GlobalScale);
            DrawCategoryBadge(values[index]);
        }
    }

    private static Vector2 CategoryBadgeSize(CardCategory category)
    {
        var textSize = ImGui.CalcTextSize(SingleCategoryLabel(category));
        return textSize + new Vector2(18, 8) * ImGuiHelpers.GlobalScale;
    }

    private static void DrawCategoryBadge(CardCategory category)
    {
        var label = SingleCategoryLabel(category);
        var size = CategoryBadgeSize(category);
        var position = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        var rounding = 4 * ImGuiHelpers.GlobalScale;
        var fill = ImGui.ColorConvertFloat4ToU32(CategoryColor(category));
        var border = ImGui.ColorConvertFloat4ToU32(new Vector4(0.92f, 0.82f, 0.56f, 0.95f));
        var textColor = ImGui.ColorConvertFloat4ToU32(Vector4.One);
        drawList.AddRectFilled(position, position + size, fill, rounding, ImDrawFlags.None);
        drawList.AddRect(position, position + size, border, rounding, ImDrawFlags.None, 1.5f * ImGuiHelpers.GlobalScale);
        var textSize = ImGui.CalcTextSize(label);
        drawList.AddText(position + (size - textSize) / 2, textColor, label);
        ImGui.Dummy(size);
    }

    private static void DrawSegmentedBorder(Vector2 minimum, Vector2 maximum, IReadOnlyList<Vector4> colors)
    {
        if (colors.Count == 0) return;
        var width = maximum.X - minimum.X;
        var height = maximum.Y - minimum.Y;
        var perimeter = 2 * (width + height);
        var drawList = ImGui.GetWindowDrawList();
        var thickness = 4 * ImGuiHelpers.GlobalScale;

        for (var index = 0; index < colors.Count; index++)
        {
            var start = perimeter * index / colors.Count;
            var end = perimeter * (index + 1) / colors.Count;
            var cursor = start;
            while (cursor < end - 0.01f)
            {
                var boundary = NextBorderCorner(cursor, width, height, perimeter);
                var next = MathF.Min(end, boundary);
                drawList.AddLine(BorderPoint(cursor, minimum, width, height), BorderPoint(next, minimum, width, height),
                    ImGui.ColorConvertFloat4ToU32(colors[index]), thickness);
                cursor = next;
            }
        }
    }

    private static float NextBorderCorner(float distance, float width, float height, float perimeter)
    {
        if (distance < width) return width;
        if (distance < width + height) return width + height;
        if (distance < 2 * width + height) return 2 * width + height;
        return perimeter;
    }

    private static Vector2 BorderPoint(float distance, Vector2 minimum, float width, float height)
    {
        if (distance <= width) return minimum + new Vector2(distance, 0);
        distance -= width;
        if (distance <= height) return minimum + new Vector2(width, distance);
        distance -= height;
        if (distance <= width) return minimum + new Vector2(width - distance, height);
        distance -= width;
        return minimum + new Vector2(0, height - distance);
    }

    private static string KeywordLabel(CardKeyword keyword) => keyword switch
    {
        CardKeyword.BlindVolunteer => "BLIND VOLUNTEER",
        CardKeyword.Choice => "CHOICE",
        CardKeyword.Random => "RANDOM",
        _ => keyword.ToString().ToUpperInvariant(),
    };

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var value = new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(value) ? "deck" : value;
    }
}
