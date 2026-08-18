using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using TruthOrDare.Models;
using TruthOrDare.Services;

namespace TruthOrDare.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private static readonly Vector4 ThemeGold = new(.72f, .56f, .27f, 1f);
    private static readonly Vector4 ThemeGoldBright = new(.92f, .76f, .42f, 1f);
    private static readonly Vector4 ThemeBurgundy = new(.34f, .07f, .12f, 1f);
    private static readonly Vector4 ThemeBurgundyHover = new(.47f, .10f, .17f, 1f);
    private static readonly Vector4 ThemePanel = new(.055f, .052f, .060f, .98f);
    private static readonly Vector4 ThemeField = new(.075f, .072f, .080f, 1f);
    private static readonly Vector4 ThemeText = new(.91f, .88f, .80f, 1f);
    private const int MaxCardText = 1000;
    private const int MaxCardTitle = 100;
    private const int MaxFlavorText = 240;
    private const int MaxDeckName = 80;
    private const int MaxDeckAuthor = 80;
    private static readonly CardCategory[] BasicCategories =
        [CardCategory.Sfw, CardCategory.Mixed, CardCategory.Nsfw, CardCategory.NsfwPlus];
    private readonly Configuration configuration;
    private readonly DeckStore store;
    private readonly DirectGameService directGame;
    private readonly RelayGameService relayGame;
    private readonly Action<Configuration> saveConfiguration;
    private readonly string cardBackPath;
    private readonly string templateDirectory;
    private readonly string artworkDirectory;
    private readonly string directGameHelpPath;
    private readonly string gameInstructionsPath;
    private readonly GameSession session = new();
    private readonly FileDialogManager fileDialogManager = new();
    private readonly IFontHandle cardFont;
    private readonly IFontHandle cardBoldFont;
    private readonly IFontHandle cardItalicFont;
    private readonly IFontHandle cardBoldItalicFont;
    private readonly IFontHandle flavorFont;
    private readonly List<Deck> decks;
    private Deck selectedDeck;
    private string status = string.Empty;
    private bool statusIsError;
    private string newDeckName = string.Empty;
    private string cardText = string.Empty;
    private string cardTitle = string.Empty;
    private string flavorText = string.Empty;
    private CardCategory cardCategory;
    private ActivityType activityType;
    private ArtworkChoice artworkChoice;
    private Guid? customArtworkId;
    private CardCategory playCategory;
    private CardKeyword? cardKeyword;
    private Guid? editingCardId;
    private Guid? pendingDeleteCardId;
    private bool requestDeleteDeck;
    private string? lastExportPath;
    private string directPublicAddress;
    private int directPort;
    private string directInvitation = string.Empty;
    private Card? directCurrentCard;
    private string directDrawer = string.Empty;
    private bool networkDrawRequested;
    private bool networkVolunteerPending;
    private bool requestPlayTab;
    private bool publicAddressDiscoveryAttempted;
    private Task<string>? publicAddressDiscoveryTask;
    private Guid? volunteerResolutionId;
    private long volunteerDeadlineUnixMilliseconds;
    private string volunteerDrawer = string.Empty;
    private string cardSearchText = string.Empty;
    private bool directScoringEnabled;
    private IReadOnlyDictionary<string, int>? finalScores;
    private IReadOnlyList<string>? finalWinners;
    private MergePreview? mergePreview;
    private readonly HashSet<int> mergeSelectedCards = [];
    private bool requestMergePreviewPopup;
    private bool statusRenderedThisFrame;
    private string relayEndpoint;
    private string relayRoomName = string.Empty;
    private string relayRoomCode = string.Empty;
    private string relayPassword = string.Empty;
    private bool relayPrivateRoom;
    private bool relayScoringEnabled;
    private Task? relayTask;

    public MainWindow(Configuration configuration, DeckStore store, DirectGameService directGame, RelayGameService relayGame, Action<Configuration> saveConfiguration,
        string cardBackPath, string templateDirectory, string artworkDirectory)
        : base("Levemetes##LevemetesMain")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(500, 470),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        this.configuration = configuration;
        this.store = store;
        this.directGame = directGame;
        this.relayGame = relayGame;
        this.saveConfiguration = saveConfiguration;
        this.cardBackPath = cardBackPath;
        this.templateDirectory = templateDirectory;
        this.artworkDirectory = artworkDirectory;
        directGameHelpPath = Path.Combine(Path.GetDirectoryName(cardBackPath) ?? string.Empty, "DirectPrivateGameHelp.txt");
        gameInstructionsPath = Path.Combine(Path.GetDirectoryName(cardBackPath) ?? string.Empty, "GameInstructions.txt");
        directPublicAddress = configuration.DirectPublicAddress;
        relayEndpoint = string.IsNullOrWhiteSpace(configuration.RelayEndpoint) ? RelayGameService.DefaultEndpoint : configuration.RelayEndpoint;
        relayGame.SetEndpoint(relayEndpoint);
        directPort = configuration.DirectPort;
        const float cardFontSize = 19f;
        cardFont = CreateCardFont(cardFontSize, false, false);
        cardBoldFont = CreateCardFont(cardFontSize, true, false);
        cardItalicFont = CreateCardFont(cardFontSize, false, true);
        cardBoldItalicFont = CreateCardFont(cardFontSize, true, true);
        flavorFont = CreateCardFont(cardFontSize - 2f, false, true);
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

    public void Dispose()
    {
        cardFont.Dispose();
        cardBoldFont.Dispose();
        cardItalicFont.Dispose();
        cardBoldItalicFont.Dispose();
        flavorFont.Dispose();
    }

    public override void Draw()
    {
        statusRenderedThisFrame = false;
        PushLevemetesTheme();
        DrawWindowTitle();
        ProcessDirectGameEvents();
        ProcessRelayGameEvents();
        DrawDeckSelector();
        GoldSeparator();
        if (ImGui.BeginTabBar("MainTabs"))
        {
            var playFlags = requestPlayTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
            if (ImGui.BeginTabItem("Play", playFlags))
            { DrawActiveTabAccent(); requestPlayTab = false; DrawPlayTab(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Cards"))
            { DrawActiveTabAccent(); DrawCardsTab(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Decks & Sharing"))
            { DrawActiveTabAccent(); DrawDecksTab(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Multiplayer"))
            { DrawActiveTabAccent(); DrawRelayGameTab(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Direct Private Game"))
            { DrawActiveTabAccent(); DrawDirectGameTab(); ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }
        if (!statusRenderedThisFrame) DrawStatus();
        DrawVolunteerPrompt();
        DrawGameResultsPopup();
        DrawMergePreviewPopup();
        DrawGameInstructionsButton();
        DrawConfirmations();
        PopLevemetesTheme();
        ImGui.SetNextWindowSizeConstraints(new Vector2(760, 520) * ImGuiHelpers.GlobalScale,
            new Vector2(float.MaxValue, float.MaxValue));
        fileDialogManager.Draw();
    }

    private static void PushLevemetesTheme()
    {
        var scale = ImGuiHelpers.GlobalScale;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14, 12) * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(10, 7) * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(9, 7) * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemInnerSpacing, new Vector2(7, 5) * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4 * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1.15f * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 6 * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 6 * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.TabRounding, 4 * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 6 * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1.5f * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 1 * scale);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(.025f, .024f, .030f, .98f));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, ThemePanel);
        ImGui.PushStyleColor(ImGuiCol.PopupBg, new Vector4(.035f, .033f, .040f, .99f));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(ThemeGold.X, ThemeGold.Y, ThemeGold.Z, .82f));
        ImGui.PushStyleColor(ImGuiCol.BorderShadow, new Vector4(0, 0, 0, .65f));
        ImGui.PushStyleColor(ImGuiCol.Text, ThemeText);
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, new Vector4(.62f, .58f, .50f, 1f));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, ThemeField);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(.12f, .10f, .11f, 1f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, new Vector4(.16f, .11f, .12f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Button, ThemeBurgundy);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ThemeBurgundyHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(.56f, .13f, .20f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(.28f, .08f, .12f, 1f));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(.42f, .12f, .17f, 1f));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, ThemeBurgundyHover);
        ImGui.PushStyleColor(ImGuiCol.Tab, new Vector4(.045f, .043f, .050f, 1f));
        ImGui.PushStyleColor(ImGuiCol.TabHovered, new Vector4(.38f, .10f, .15f, 1f));
        ImGui.PushStyleColor(ImGuiCol.TabActive, new Vector4(.40f, .075f, .13f, 1f));
        ImGui.PushStyleColor(ImGuiCol.TabUnfocusedActive, new Vector4(.34f, .065f, .11f, 1f));
        ImGui.PushStyleColor(ImGuiCol.CheckMark, ThemeGoldBright);
        ImGui.PushStyleColor(ImGuiCol.SliderGrab, ThemeGold);
        ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, ThemeGoldBright);
        ImGui.PushStyleColor(ImGuiCol.Separator, new Vector4(ThemeGold.X, ThemeGold.Y, ThemeGold.Z, .52f));
        ImGui.PushStyleColor(ImGuiCol.SeparatorHovered, ThemeGoldBright);
        ImGui.PushStyleColor(ImGuiCol.SeparatorActive, ThemeGoldBright);
        ImGui.PushStyleColor(ImGuiCol.ResizeGrip, new Vector4(ThemeGold.X, ThemeGold.Y, ThemeGold.Z, .25f));
        ImGui.PushStyleColor(ImGuiCol.ResizeGripHovered, ThemeGold);
        ImGui.PushStyleColor(ImGuiCol.ResizeGripActive, ThemeGoldBright);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, new Vector4(.025f, .024f, .030f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, new Vector4(.28f, .22f, .15f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, ThemeGold);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive, ThemeGoldBright);
    }

    private static void PopLevemetesTheme()
    {
        ImGui.PopStyleColor(33);
        ImGui.PopStyleVar(12);
    }

    private static void DrawWindowTitle()
    {
        var text = "L E V E M E T E S";
        var version = typeof(Plugin).Assembly.GetName().Version;
        var versionText = version is null ? string.Empty : $"v{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        var titleY = ImGui.GetCursorPosY();
        ImGui.SetWindowFontScale(1.35f);
        var width = ImGui.CalcTextSize(text).X;
        ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX(), (ImGui.GetWindowWidth() - width) / 2));
        ImGui.TextColored(ThemeGoldBright, text);
        ImGui.SetWindowFontScale(1f);
        var nextLineY = ImGui.GetCursorPosY();
        if (versionText.Length > 0)
        {
            var versionWidth = ImGui.CalcTextSize(versionText).X;
            ImGui.SetCursorPos(new Vector2(
                MathF.Max(ImGui.GetCursorPosX(), ImGui.GetWindowWidth() - versionWidth - 18 * ImGuiHelpers.GlobalScale),
                titleY + 4 * ImGuiHelpers.GlobalScale));
            ImGui.TextDisabled(versionText);
            ImGui.SetCursorPosY(nextLineY);
        }
        GoldSeparator();
    }

    private static void GoldSeparator()
    {
        ImGui.PushStyleColor(ImGuiCol.Separator, ThemeGold);
        ImGui.Separator();
        ImGui.PopStyleColor();
    }

    private static void DrawActiveTabAccent()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetWindowDrawList();
        var minimum = ImGui.GetItemRectMin();
        var maximum = ImGui.GetItemRectMax();
        var gold = ImGui.ColorConvertFloat4ToU32(new Vector4(.84f, .65f, .27f, 1f));
        drawList.AddRect(minimum + new Vector2(scale), maximum - new Vector2(scale, 0), gold,
            4 * scale, ImDrawFlags.None, 1.4f * scale);
        var center = new Vector2((minimum.X + maximum.X) / 2, maximum.Y + 2.5f * scale);
        var radius = 3.5f * scale;
        drawList.AddQuadFilled(new Vector2(center.X, center.Y - radius), new Vector2(center.X + radius, center.Y),
            new Vector2(center.X, center.Y + radius), new Vector2(center.X - radius, center.Y), gold);
        drawList.AddCircleFilled(center, 1.15f * scale,
            ImGui.ColorConvertFloat4ToU32(new Vector4(.98f, .88f, .56f, 1f)));
    }

    private static void SectionHeading(string title)
    {
        ImGui.Spacing();
        ImGui.TextColored(ThemeGoldBright, title.ToUpperInvariant());
        ImGui.SameLine();
        ImGui.Separator();
        ImGui.Spacing();
    }

    private static void FormSectionHeading(string title)
    {
        ImGui.Spacing();
        var start = ImGui.GetCursorScreenPos();
        ImGui.TextColored(ThemeGoldBright, $"✦ {title}");
        var textEnd = ImGui.GetItemRectMax();
        var lineY = (ImGui.GetItemRectMin().Y + textEnd.Y) / 2;
        var lineStart = textEnd.X + 8 * ImGuiHelpers.GlobalScale;
        var lineEnd = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
        if (lineEnd > lineStart)
            ImGui.GetWindowDrawList().AddLine(new Vector2(lineStart, lineY), new Vector2(lineEnd, lineY),
                ImGui.ColorConvertFloat4ToU32(new Vector4(ThemeGold.X, ThemeGold.Y, ThemeGold.Z, .52f)),
                ImGuiHelpers.GlobalScale);
    }

    private static void DrawCharacterCounter(int used, int maximum, string? note = null)
    {
        var text = $"{used} / {maximum} used  •  {Math.Max(0, maximum - used)} remaining" +
            (string.IsNullOrWhiteSpace(note) ? string.Empty : $"  •  {note}");
        var width = ImGui.CalcTextSize(text).X;
        ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - width));
        var nearLimit = used >= maximum * .9f;
        ImGui.TextColored(nearLimit
            ? new Vector4(1f, .55f, .32f, 1f)
            : new Vector4(.62f, .55f, .42f, 1f), text);
    }

    private void DrawDeckSelector()
    {
        var networkConnected = directGame.IsConnected || relayGame.IsConnected;
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(.065f, .060f, .068f, 1f));
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(ThemeGoldBright, "DECK");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(260 * ImGuiHelpers.GlobalScale);
        if (networkConnected) ImGui.BeginDisabled();
        if (ImGui.BeginCombo("##Deck", selectedDeck.Name))
        {
            foreach (var deck in decks)
            {
                if (ImGui.Selectable($"{deck.Name} ({deck.Cards.Count})##{deck.Id}", deck.Id == selectedDeck.Id)) SelectDeck(deck);
            }
            ImGui.EndCombo();
        }
        if (networkConnected) ImGui.EndDisabled();
        ImGui.SameLine();
        if (!string.IsNullOrWhiteSpace(selectedDeck.Author))
        {
            ImGui.TextDisabled($"by {selectedDeck.Author}");
            ImGui.SameLine();
        }
        var deckDate = selectedDeck.LastEditedAtUtc ?? selectedDeck.ImportedAtUtc;
        if (deckDate is DateTimeOffset timestamp)
        {
            ImGui.TextDisabled($"• {(selectedDeck.LastEditedAtUtc is null ? "Imported" : "Updated")} {timestamp.ToLocalTime():MMM d, yyyy}");
            ImGui.SameLine();
        }
        var remaining = directGame.IsConnected ? directGame.Remaining : relayGame.IsConnected ? relayGame.Game?.Remaining ?? 0 : session.Remaining;
        ImGui.TextDisabled($"• {remaining} remaining");
        ImGui.PopStyleColor();
    }

    private void DrawPlayTab()
    {
        var relayState = relayGame.Game;
        var networkConnected = directGame.IsConnected || relayGame.IsConnected;
        ImGui.Spacing();
        SectionHeading("Play a Levemete");
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Intensity (Heat) Category");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(180 * ImGuiHelpers.GlobalScale);
        if (networkConnected) ImGui.BeginDisabled();
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
        if (networkConnected) ImGui.EndDisabled();
        var categoryCount = selectedDeck.Cards.Count(card => card.Category.HasFlag(playCategory));
        ImGui.SameLine();
        ImGui.TextDisabled($"{categoryCount} cards");
        if (directGame.IsConnected)
        {
            ImGui.Spacing();
            ImGui.TextUnformatted($"Direct Private Game Players ({directGame.Players.Count}/8)");
            foreach (var player in directGame.Players)
            {
                var displayName = RemoveHostSuffix(player);
                var isCurrent = directGame.GameStarted && displayName.Equals(directGame.CurrentPlayer, StringComparison.OrdinalIgnoreCase);
                var isAwaitingScore = directGame.AwaitingScores &&
                    directGame.EligibleScoreVoters.Contains(displayName, StringComparer.OrdinalIgnoreCase) &&
                    !directGame.SubmittedVoters.Contains(displayName, StringComparer.OrdinalIgnoreCase);
                var status = isCurrent
                    ? " - current turn"
                    : isAwaitingScore ? " - awaiting score" : string.Empty;
                var playerColor = isCurrent
                    ? new Vector4(.95f, .78f, .30f, 1f)
                    : isAwaitingScore ? new Vector4(1f, .60f, .25f, 1f) : Vector4.One;
                ImGui.Bullet();
                ImGui.SameLine();
                ImGui.TextColored(playerColor, player + status);
            }
            if (!directGame.IsHost) ImGui.TextDisabled("Only the host can Shuffle / Reset the shared deck.");
        }
        else if (relayGame.IsConnected)
        {
            ImGui.Spacing();
            ImGui.TextUnformatted($"Relay Multiplayer Players ({relayGame.Players.Count}/16)");
            foreach (var player in relayGame.Players)
            {
                var isCurrent = relayState?.Started == true && player.Name.Equals(relayState.CurrentPlayer, StringComparison.OrdinalIgnoreCase);
                var isAwaitingScore = !string.IsNullOrWhiteSpace(relayState?.ScoringDrawer) &&
                    relayState.EligibleVoters.Contains(player.Name, StringComparer.OrdinalIgnoreCase) &&
                    !relayState.SubmittedVoters.Contains(player.Name, StringComparer.OrdinalIgnoreCase);
                var suffix = isCurrent ? " - current turn" : isAwaitingScore ? " - awaiting score" : player.Connected ? string.Empty : " - disconnected";
                var color = isCurrent ? new Vector4(.95f, .78f, .30f, 1f) : isAwaitingScore
                    ? new Vector4(1f, .60f, .25f, 1f) : player.Connected ? Vector4.One : new Vector4(.6f, .6f, .6f, 1f);
                ImGui.Bullet(); ImGui.SameLine();
                ImGui.TextColored(color, player.Name + (player.Host ? " (Host)" : string.Empty) + suffix);
            }
            if (!relayGame.IsHost) ImGui.TextDisabled("Only the host can Shuffle / Reset the shared deck.");
        }
        ImGui.Dummy(new Vector2(0, ImGui.GetTextLineHeightWithSpacing() * 2));
        if (networkConnected && !string.IsNullOrWhiteSpace(directDrawer))
        {
            var drawerLabel = $"{directDrawer} drew this card";
            ImGui.SetWindowFontScale(1.15f);
            CenteredText(drawerLabel, ThemeGoldBright);
            ImGui.SetWindowFontScale(1f);
            ImGui.Spacing();
        }
        var availablePlayWidth = ImGui.GetContentRegionAvail().X;
        var actionRailWidth = MathF.Min(300 * ImGuiHelpers.GlobalScale, MathF.Max(245 * ImGuiHelpers.GlobalScale, availablePlayWidth * .34f));
        var actionRailGap = 16 * ImGuiHelpers.GlobalScale;
        var width = MathF.Min(500 * ImGuiHelpers.GlobalScale,
            MathF.Max(250 * ImGuiHelpers.GlobalScale, availablePlayWidth - actionRailWidth - actionRailGap));
        var cardSize = new Vector2(width, width * 1.30f);
        var playGroupWidth = width + actionRailGap + actionRailWidth;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, (availablePlayWidth - playGroupWidth) / 2));
        ImGui.BeginGroup();
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 0);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 12 * ImGuiHelpers.GlobalScale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.72f, 0.59f, 0.27f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.08f, 0.07f, 0.12f, 0.98f));
        var displayedCard = networkConnected ? directCurrentCard : session.CurrentCard;
        if (ImGui.BeginChild("CardFace", cardSize, true))
        {
            if (displayedCard is null)
            {
                var texture = Plugin.TextureProvider.GetFromFile(CurrentCardBackPath()).GetWrapOrDefault();
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
                DrawRevealedTemplate(displayedCard, cardSize);
            }
        }
        ImGui.EndChild();
        if (displayedCard is null)
            DrawSegmentedBorder(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), [new Vector4(0.72f, 0.59f, 0.27f, 1f)]);
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(3);

        DrawPlayStatus();
        ImGui.EndGroup();
        ImGui.SameLine(0, actionRailGap);
        ImGui.BeginChild("##PlayActionRail", new Vector2(actionRailWidth, cardSize.Y), false);

        if (directGame.IsConnected && directGame.ScoringEnabled)
        {
            ImGui.Spacing();
            FormSectionHeading("SCORING");
            var local = GetLocalCharacterLabel() ?? string.Empty;
            if (directGame.AwaitingScores)
            {
                ImGui.TextUnformatted($"Scoring {directGame.ScoringDrawer}'s turn • {directGame.SubmittedVoters.Count}/{directGame.EligibleScoreVoterCount} submitted");
                var mayVote = !local.Equals(directGame.ScoringDrawer, StringComparison.OrdinalIgnoreCase) &&
                    !directGame.SubmittedVoters.Contains(local, StringComparer.OrdinalIgnoreCase);
                if (!mayVote) ImGui.BeginDisabled();
                for (var score = 0; score <= 5; score++)
                {
                    if (score > 0 && score % 3 != 0) ImGui.SameLine();
                    if (ImGui.Button(score.ToString(), new Vector2(38, 32) * ImGuiHelpers.GlobalScale)) directGame.SubmitScore(score);
                }
                if (!mayVote) ImGui.EndDisabled();
            }
            foreach (var pair in directGame.Scores.OrderByDescending(pair => pair.Value))
                ImGui.TextDisabled($"{pair.Key}: {pair.Value}");
        }
        else if (relayGame.IsConnected && relayState?.ScoringEnabled == true)
        {
            ImGui.Spacing();
            FormSectionHeading("SCORING");
            var local = GetLocalCharacterLabel() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(relayState.ScoringDrawer))
            {
                ImGui.TextWrapped($"Scoring {relayState.ScoringDrawer}'s turn • {relayState.SubmittedVoters.Count}/{relayState.EligibleVoters.Count} submitted");
                var mayVote = relayState.EligibleVoters.Contains(local, StringComparer.OrdinalIgnoreCase) &&
                    !relayState.SubmittedVoters.Contains(local, StringComparer.OrdinalIgnoreCase);
                if (!mayVote) ImGui.BeginDisabled();
                for (var score = 0; score <= 5; score++)
                {
                    if (score > 0 && score % 3 != 0) ImGui.SameLine();
                    if (ImGui.Button(score.ToString(), new Vector2(38, 32) * ImGuiHelpers.GlobalScale))
                        relayTask = relayGame.SubmitScoreAsync(score);
                }
                if (!mayVote) ImGui.EndDisabled();
            }
            foreach (var pair in relayState.Scores.OrderByDescending(pair => pair.Value))
                ImGui.TextDisabled($"{pair.Key}: {pair.Value}");
        }

        if (directGame.IsConnected && directGame.AwaitingTieBreak)
        {
            ImGui.Spacing();
            FormSectionHeading("TIE-BREAK VOTE");
            ImGui.TextWrapped("Players outside the tie may choose the winner. If the deciding vote remains tied, victory is shared.");
            var local = GetLocalCharacterLabel() ?? string.Empty;
            var mayVote = !directGame.TieBreakCandidates.Contains(local, StringComparer.OrdinalIgnoreCase) &&
                !directGame.SubmittedTieBreakVoters.Contains(local, StringComparer.OrdinalIgnoreCase);
            if (!mayVote) ImGui.BeginDisabled();
            foreach (var candidate in directGame.TieBreakCandidates)
            {
                if (ImGui.Button($"Vote for {candidate}", new Vector2(-1, 0))) directGame.SubmitTieBreakVote(candidate);
            }
            if (!mayVote) ImGui.EndDisabled();
        }
        else if (relayGame.IsConnected && relayState is { TieBreakCandidates.Count: > 0 })
        {
            ImGui.Spacing();
            FormSectionHeading("TIE-BREAK VOTE");
            ImGui.TextWrapped("Players outside the tie may choose the winner. If the deciding vote remains tied, victory is shared.");
            var local = GetLocalCharacterLabel() ?? string.Empty;
            var mayVote = relayState.EligibleTieVoters.Contains(local, StringComparer.OrdinalIgnoreCase) &&
                !relayState.SubmittedTieVoters.Contains(local, StringComparer.OrdinalIgnoreCase);
            if (!mayVote) ImGui.BeginDisabled();
            foreach (var candidate in relayState.TieBreakCandidates)
                if (ImGui.Button($"Vote for {candidate}", new Vector2(-1, 0))) relayTask = relayGame.SubmitTieBreakAsync(candidate);
            if (!mayVote) ImGui.EndDisabled();
        }

        ImGui.Spacing();
        FormSectionHeading("CARD ACTIONS");
        var railButtonSize = new Vector2(-1, 34 * ImGuiHelpers.GlobalScale);
        if (displayedCard is null) ImGui.BeginDisabled();
        if (ImGui.Button("Copy Card Text", railButtonSize) && displayedCard is Card cardToCopy)
        {
            ImGui.SetClipboardText(StripFormatting(cardToCopy.Text));
            SetStatus("Card text copied to the clipboard.");
        }
        if (displayedCard is null) ImGui.EndDisabled();

        ImGui.Spacing();
        var localPlayer = GetLocalCharacterLabel();
        var turnReady = !networkConnected ||
            (directGame.IsConnected
                ? directGame.GameStarted && string.Equals(directGame.CurrentPlayer, localPlayer, StringComparison.OrdinalIgnoreCase)
                : relayState?.Started == true && string.Equals(relayState.CurrentPlayer, localPlayer, StringComparison.OrdinalIgnoreCase));
        var remainingCards = directGame.IsConnected ? directGame.Remaining : relayGame.IsConnected ? relayState?.Remaining ?? 0 : session.Remaining;
        var awaitingScores = directGame.IsConnected ? directGame.AwaitingScores : relayGame.IsConnected && !string.IsNullOrWhiteSpace(relayState?.ScoringDrawer);
        var awaitingVolunteer = directGame.IsConnected
            ? networkVolunteerPending || directGame.AwaitingVolunteer
            : relayGame.IsConnected && relayState?.PendingVolunteer is not null;
        var canDraw = categoryCount > 0 && remainingCards > 0 && turnReady && !awaitingScores && !awaitingVolunteer && !networkDrawRequested;
        if (!canDraw) ImGui.BeginDisabled();
        if (ImGui.Button("Draw", railButtonSize))
        {
            if (directGame.IsConnected) { networkDrawRequested = true; directGame.RequestDraw(); }
            else if (relayGame.IsConnected) { networkDrawRequested = true; relayTask = relayGame.RequestDrawAsync(); }
            else session.Draw(selectedDeck, playCategory);
        }
        if (!canDraw) ImGui.EndDisabled();
        ImGui.Spacing();
        var networkHost = directGame.IsConnected ? directGame.IsHost : relayGame.IsConnected && relayGame.IsHost;
        if (networkConnected && !networkHost) ImGui.BeginDisabled();
        if (ImGui.Button("Shuffle / Reset", railButtonSize))
        {
            if (directGame.IsConnected) directGame.ResetSharedPile(selectedDeck);
            else if (relayGame.IsConnected) relayTask = relayGame.ResetAsync();
            else { session.Reset(selectedDeck, playCategory); SetStatus("Draw pile shuffled."); }
        }
        if (networkConnected && !networkHost) ImGui.EndDisabled();
        if (networkConnected && !networkHost && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Only the host can shuffle and reset the shared deck.");
        if (networkConnected)
        {
            ImGui.Spacing();
            if (!networkHost) ImGui.BeginDisabled();
            if (ImGui.Button("Start New Game", railButtonSize))
            {
                directCurrentCard = null;
                directDrawer = string.Empty;
                networkDrawRequested = false;
                networkVolunteerPending = false;
                if (directGame.IsConnected) TryAction(() => directGame.StartNewGame(selectedDeck));
                else relayTask = relayGame.StartNewGameAsync();
            }
            if (!networkHost) ImGui.EndDisabled();
            ImGui.Spacing();
            var gameActive = directGame.IsConnected ? directGame.GameStarted : relayState?.Started == true;
            if (!networkHost || !gameActive) ImGui.BeginDisabled();
            if (ImGui.Button("End Game", railButtonSize))
            {
                if (directGame.IsConnected) directGame.EndGame(); else relayTask = relayGame.EndGameAsync();
            }
            if (!networkHost || !gameActive) ImGui.EndDisabled();
        }
        if (categoryCount > 0 && remainingCards == 0)
            ImGui.TextDisabled(networkConnected && !networkHost ? "No shared cards remain. The host must shuffle and reset." : "No cards remain in this category. Shuffle / Reset to play again.");
        else if (networkConnected && !(directGame.IsConnected ? directGame.GameStarted : relayState?.Started == true))
            ImGui.TextDisabled("Waiting for the host to start the game.");
        else if (networkConnected && !turnReady)
            ImGui.TextDisabled($"Waiting for {(directGame.IsConnected ? directGame.CurrentPlayer : relayState?.CurrentPlayer)} to draw.");
        if (directGame.IsConnected && directGame.IsHost)
        {
            ImGui.Spacing();
            var canForcePass = directGame.AwaitingScores &&
                directGame.SubmittedVoters.Count < directGame.EligibleScoreVoterCount;
            if (!canForcePass) ImGui.BeginDisabled();
            if (ImGui.Button("Force Pass", railButtonSize)) directGame.ForcePassScores();
            if (!canForcePass) ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Assign 3 points for each eligible player who has not scored this turn.");
        }
        else if (relayGame.IsConnected && relayGame.IsHost)
        {
            ImGui.Spacing();
            var canForcePass = !string.IsNullOrWhiteSpace(relayState?.ScoringDrawer) &&
                relayState!.SubmittedVoters.Count < relayState.EligibleVoters.Count;
            if (!canForcePass) ImGui.BeginDisabled();
            if (ImGui.Button("Force Pass", railButtonSize)) relayTask = relayGame.ForcePassAsync();
            if (!canForcePass) ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Assign 3 points for each eligible player who has not scored this turn.");
        }
        ImGui.EndChild();
    }

    private void DrawCardsTab()
    {
        ImGui.Spacing();
        if (directGame.IsConnected || relayGame.IsConnected)
        {
            SectionHeading("Cards");
            ImGui.TextWrapped("The synchronized deck is locked while a multiplayer room is connected. Leave the room to edit cards.");
            return;
        }

        DrawCardEditorDeckHeader();
        var available = ImGui.GetContentRegionAvail();
        var gap = 12 * ImGuiHelpers.GlobalScale;
        var leftWidth = MathF.Max(300 * ImGuiHelpers.GlobalScale, available.X * .34f);
        var rightWidth = MathF.Max(250 * ImGuiHelpers.GlobalScale, available.X * .25f);

        ImGui.BeginChild("CardEditorLeft", new Vector2(leftWidth, available.Y), true);
        DrawLiveCardPreview();
        ImGui.EndChild();

        ImGui.SameLine(0, gap);
        ImGui.BeginChild("CardEditorCenter", new Vector2(MathF.Max(300 * ImGuiHelpers.GlobalScale, available.X - leftWidth - rightWidth - gap * 2), available.Y), true);
        DrawCardEditorForm();
        ImGui.EndChild();

        ImGui.SameLine(0, gap);
        ImGui.BeginChild("CardEditorRight", new Vector2(0, available.Y), true);
        DrawExistingCardList();
        ImGui.EndChild();
    }

    private void DrawCardEditorDeckHeader()
    {
        var height = 76 * ImGuiHelpers.GlobalScale;
        ImGui.BeginChild("CardEditorDeckHeader", new Vector2(0, height), true, ImGuiWindowFlags.NoScrollbar);
        var texture = Plugin.TextureProvider.GetFromFile(CurrentCardBackPath()).GetWrapOrDefault();
        var thumbnail = new Vector2(42, 48) * ImGuiHelpers.GlobalScale;
        if (texture is not null) ImGui.Image(texture.Handle, thumbnail);
        else ImGui.Dummy(thumbnail);
        ImGui.SameLine();
        ImGui.BeginGroup();
        ImGui.TextColored(ThemeGoldBright, $"Deck: {selectedDeck.Name}");
        ImGui.TextDisabled(string.IsNullOrWhiteSpace(selectedDeck.Author)
            ? $"{selectedDeck.Cards.Count} cards"
            : $"by {selectedDeck.Author}  •  {selectedDeck.Cards.Count} cards");
        ImGui.EndGroup();
        ImGui.SameLine();
        var backs = store.GetCardBackChoices();
        var selectedBack = backs.FirstOrDefault(choice =>
            choice.Sha256.Equals(selectedDeck.CustomCardBackSha256, StringComparison.OrdinalIgnoreCase));
        ImGui.SetNextItemWidth(210 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("##CardBackChoice", selectedBack?.Name ?? "Default Card Back"))
        {
            if (ImGui.Selectable("Default Card Back", string.IsNullOrWhiteSpace(selectedDeck.CustomCardBackSha256)))
            {
                store.RemoveCustomCardBack(selectedDeck);
                SetStatus("Restored the default card back for this deck.");
            }
            if (backs.Count > 0) ImGui.Separator();
            foreach (var choice in backs)
                if (ImGui.Selectable(choice.Name, selectedBack?.Sha256 == choice.Sha256)) TryAction(() =>
                {
                    store.SelectCustomCardBack(selectedDeck, choice.Sha256);
                    SetStatus($"Selected card back: {choice.Name}.");
                });
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (ImGui.Button("Change Card Back…")) OpenCardBackDialog();
        ImGui.EndChild();
        ImGui.Spacing();
    }

    private void DrawLiveCardPreview()
    {
        ImGui.TextColored(ThemeGoldBright, "LIVE CARD PREVIEW");
        ImGui.TextDisabled("Updates as you edit.");
        var width = MathF.Min(ImGui.GetContentRegionAvail().X, 500 * ImGuiHelpers.GlobalScale);
        var cardSize = new Vector2(width, width * 1.30f);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, (ImGui.GetContentRegionAvail().X - width) / 2));
        var preview = new Card
        {
            Title = string.IsNullOrWhiteSpace(cardTitle) ? "Untitled Levemete" : cardTitle,
            Activity = activityType,
            Artwork = artworkChoice,
            CustomArtworkId = customArtworkId,
            Category = cardCategory == CardCategory.None ? CardCategory.Sfw : cardCategory,
            Keyword = cardKeyword,
            Text = string.IsNullOrWhiteSpace(cardText) ? "Your card text will appear here as you type." : cardText,
            FlavorText = flavorText,
        };
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        if (ImGui.BeginChild("EditorCardPreview", cardSize, true, ImGuiWindowFlags.NoScrollbar))
            DrawRevealedTemplate(preview, cardSize);
        ImGui.EndChild();
        ImGui.PopStyleVar();
    }

    private void DrawExistingCardList()
    {
        ImGui.TextColored(ThemeGoldBright, $"EXISTING CARDS ({selectedDeck.Cards.Count})");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##CardSearch", "Search title, text, activity, keyword…", ref cardSearchText, 160);
        var query = cardSearchText.Trim();
        var cards = selectedDeck.Cards.Where(card => CardMatchesSearch(card, query)).ToList();
        ImGui.TextDisabled(string.IsNullOrEmpty(query) ? "Select Edit to load a card." : $"{cards.Count} matching card{(cards.Count == 1 ? string.Empty : "s")}");
        if (!ImGui.BeginChild("CardList", Vector2.Zero, false)) { ImGui.EndChild(); return; }
        foreach (var card in cards)
        {
            ImGui.PushID(card.Id.ToString());
            ImGui.TextUnformatted(card.Title);
            ImGui.SameLine();
            ImGui.TextDisabled(ActivityLabel(card.Activity));
            DrawCategoryLabelsInline(card.Category);
            if (card.Keyword is CardKeyword keyword)
            {
                ImGui.SameLine();
                ImGui.TextColored(ThemeGoldBright, KeywordLabel(keyword));
            }
            if (ImGui.SmallButton("Edit")) BeginEdit(card);
            ImGui.SameLine();
            if (ImGui.SmallButton("Delete")) pendingDeleteCardId = card.Id;
            ImGui.PopID();
            GoldSeparator();
        }
        ImGui.EndChild();
    }

    private static bool CardMatchesSearch(Card card, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        return card.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || StripFormatting(card.Text).Contains(query, StringComparison.OrdinalIgnoreCase)
            || card.FlavorText.Contains(query, StringComparison.OrdinalIgnoreCase)
            || ActivityLabel(card.Activity).Contains(query, StringComparison.OrdinalIgnoreCase)
            || CategoryLabel(card.Category).Contains(query, StringComparison.OrdinalIgnoreCase)
            || (card.Keyword is CardKeyword keyword && KeywordLabel(keyword).Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private void DrawCardEditorForm()
    {
        ImGui.Spacing();
        ImGui.TextColored(ThemeGoldBright, editingCardId is null ? "CREATE A CARD" : "EDIT CARD");
        ImGui.Spacing();
        ImGui.TextUnformatted("Deck author");
        ImGui.SameLine();
        var editedAuthor = selectedDeck.Author;
        ImGui.SetNextItemWidth(260 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputTextWithHint("##CardEditorDeckAuthor", "Optional creator name", ref editedAuthor, MaxDeckAuthor + 1))
            selectedDeck.Author = editedAuthor;
        ImGui.SameLine();
        if (ImGui.Button("Save Author##CardEditor")) SaveDeck("Deck author saved.");
        GoldSeparator();
        FormSectionHeading("CARD DETAILS");
        ImGui.InputTextWithHint("##CardTitle", "Levemete title", ref cardTitle, MaxCardTitle + 1);
        DrawCharacterCounter(cardTitle.Length, MaxCardTitle);
        ImGui.TextUnformatted("Activity type");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(250 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("##ActivityType", ActivityLabel(activityType)))
        {
            foreach (var activity in Enum.GetValues<ActivityType>())
                if (ImGui.Selectable(ActivityLabel(activity), activityType == activity)) activityType = activity;
            ImGui.EndCombo();
        }
        ImGui.TextUnformatted("Artwork");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(280 * ImGuiHelpers.GlobalScale);
        var artworkCaption = customArtworkId is Guid selectedCustom
            ? selectedDeck.CustomArtwork.FirstOrDefault(asset => asset.Id == selectedCustom)?.Name ?? "Missing custom artwork"
            : ArtworkLabel(artworkChoice);
        if (ImGui.BeginCombo("##ArtworkChoice", artworkCaption))
        {
            foreach (var group in BasicCategories)
            {
                ImGui.TextDisabled(SingleCategoryLabel(group));
                foreach (var artwork in Enum.GetValues<ArtworkChoice>().Where(value => ArtworkCategory(value) == group))
                {
                    if (ImGui.Selectable(ArtworkLabel(artwork), customArtworkId is null && artworkChoice == artwork))
                    { artworkChoice = artwork; customArtworkId = null; }
                }
                if (group != CardCategory.NsfwPlus) ImGui.Separator();
            }
            if (selectedDeck.CustomArtwork.Count > 0)
            {
                ImGui.Separator();
                ImGui.TextDisabled("CUSTOM");
                foreach (var asset in selectedDeck.CustomArtwork)
                    if (ImGui.Selectable(asset.Name, customArtworkId == asset.Id)) customArtworkId = asset.Id;
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (ImGui.Button("Add Custom Image...")) OpenArtworkDialog();
        if (customArtworkId is Guid removable)
        {
            ImGui.SameLine();
            if (ImGui.Button("Remove Custom Image")) TryAction(() =>
            {
                store.DeleteCustomArtwork(selectedDeck, removable);
                customArtworkId = null;
                SetStatus("Custom artwork removed.");
            });
        }
        FormSectionHeading("INTENSITY — SELECT ONE OR MORE");
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
        FormSectionHeading("OPTIONAL KEYWORD");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(190 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("##Keyword", cardKeyword is null ? "None" : KeywordLabel(cardKeyword.Value)))
        {
            if (ImGui.Selectable("None", cardKeyword is null)) cardKeyword = null;
            foreach (var keyword in Enum.GetValues<CardKeyword>())
                if (ImGui.Selectable(KeywordLabel(keyword), cardKeyword == keyword)) cardKeyword = keyword;
            ImGui.EndCombo();
        }
        FormSectionHeading("CARD TEXT");
        ImGui.SameLine();
        if (ImGui.Button("Bold")) AppendFormatting("b", "bold text");
        ImGui.SameLine();
        if (ImGui.Button("Italic")) AppendFormatting("i", "italic text");
        ImGui.SameLine();
        if (ImGui.Button("Underline")) AppendFormatting("u", "underlined text");
        ImGui.SameLine();
        if (ImGui.Button("Center Line")) AppendFormatting("c", "centered sentence");
        ImGui.TextDisabled("Formatting buttons insert editable text at the end. Styles can be combined by nesting their tags.");
        ImGui.InputTextMultiline("##CardText", ref cardText, MaxCardText + 1, new Vector2(-1, 85 * ImGuiHelpers.GlobalScale));
        DrawCharacterCounter(cardText.Length, MaxCardText, "storage limit; preview auto-fits");
        FormSectionHeading("FLAVOR TEXT");
        ImGui.TextDisabled("Shown separately at the bottom of the card. It is not included when card text is copied.");
        ImGui.InputTextMultiline("##FlavorText", ref flavorText, MaxFlavorText + 1,
            new Vector2(-1, 58 * ImGuiHelpers.GlobalScale));
        DrawCharacterCounter(flavorText.Length, MaxFlavorText, "storage limit; preview auto-fits");
        var visibleCardText = StripFormatting(cardText);
        var valid = cardCategory != CardCategory.None && !string.IsNullOrWhiteSpace(cardTitle) && cardTitle.Trim().Length <= MaxCardTitle
            && !string.IsNullOrWhiteSpace(visibleCardText) && cardText.Trim().Length <= MaxCardText;
        if (!valid) ImGui.BeginDisabled();
        if (ImGui.Button(editingCardId is null ? "Add Card" : "Save Changes")) SaveCard();
        if (!valid) ImGui.EndDisabled();
        if (editingCardId is not null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) ClearEditor();
        }
    }

    private void DrawArtworkPreview()
    {
        ImGui.Spacing();
        ImGui.TextUnformatted("Artwork preview");
        var path = ResolveArtworkPath(customArtworkId, artworkChoice);
        var texture = Plugin.TextureProvider.GetFromFile(path).GetWrapOrDefault();
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var width = MathF.Min(availableWidth, 360f * ImGuiHelpers.GlobalScale);
        var size = new Vector2(width, width / 2.23f);
        var startX = ImGui.GetCursorPosX() + MathF.Max(0, (availableWidth - width) / 2f);
        ImGui.SetCursorPosX(startX);

        if (texture is null)
        {
            ImGui.Dummy(size);
            var minimum = ImGui.GetItemRectMin();
            ImGui.GetWindowDrawList().AddRectFilled(minimum, minimum + size,
                ImGui.ColorConvertFloat4ToU32(new Vector4(.12f, .09f, .08f, 1f)));
            ImGui.SetCursorScreenPos(minimum + new Vector2(10, 10) * ImGuiHelpers.GlobalScale);
            ImGui.TextDisabled("Artwork preview unavailable");
            return;
        }

        ImGui.Image(texture.Handle, size, new Vector2(0, .165f), new Vector2(1, .835f));
        var imageMinimum = ImGui.GetItemRectMin();
        ImGui.GetWindowDrawList().AddRect(imageMinimum, imageMinimum + size,
            ImGui.ColorConvertFloat4ToU32(new Vector4(.65f, .48f, .18f, 1f)),
            5f * ImGuiHelpers.GlobalScale, ImDrawFlags.None, 2f * ImGuiHelpers.GlobalScale);
        ImGui.Spacing();
    }

    private void DrawDecksTab()
    {
        ImGui.Spacing();
        SectionHeading("Decks & Sharing");
        if (directGame.IsConnected || relayGame.IsConnected)
        {
            ImGui.TextWrapped("Deck management and sharing are locked while a multiplayer room is connected. Leave the room to make changes.");
            return;
        }
        FormSectionHeading("CREATE DECK");
        ImGui.SetNextItemWidth(300 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##NewDeck", "Deck name", ref newDeckName, MaxDeckName + 1);
        ImGui.SameLine();
        if (ImGui.Button("Create") && !string.IsNullOrWhiteSpace(newDeckName)) CreateDeck();
        FormSectionHeading("SELECTED DECK");
        var editedName = selectedDeck.Name;
        ImGui.SetNextItemWidth(300 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("Name", ref editedName, MaxDeckName + 1) && !string.IsNullOrWhiteSpace(editedName)) selectedDeck.Name = editedName;
        ImGui.SameLine();
        if (ImGui.Button("Save Name")) SaveDeck("Deck name saved.");
        var deckAuthor = selectedDeck.Author;
        ImGui.SetNextItemWidth(300 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputTextWithHint("Author##DeckDetails", "Optional creator name", ref deckAuthor, MaxDeckAuthor + 1))
            selectedDeck.Author = deckAuthor;
        ImGui.SameLine();
        if (ImGui.Button("Save Author##DeckDetails")) SaveDeck("Deck author saved.");
        ImGui.SameLine();
        if (decks.Count <= 1) ImGui.BeginDisabled();
        if (ImGui.Button("Delete Deck")) requestDeleteDeck = true;
        if (decks.Count <= 1) ImGui.EndDisabled();
        FormSectionHeading("IMPORT & MERGE");
        ImGui.TextWrapped("Import a portable .levemetesdeck bundle (including custom artwork) or a legacy JSON deck. When merging, review its cards and choose exactly which ones to add.");
        if (ImGui.Button("Import as New Deck...")) OpenImportDialog(merge: false);
        ImGui.SameLine();
        if (ImGui.Button("Merge into Selected...")) OpenImportDialog(merge: true);
        FormSectionHeading("EXPORT SELECTED DECK");
        ImGui.TextDisabled("Creates one shareable file containing the deck and its custom artwork.");
        if (ImGui.Button("Export Selected Deck...")) OpenExportDialog();
        ImGui.SameLine();
        if (lastExportPath is null) ImGui.BeginDisabled();
        if (ImGui.Button($"{FontAwesomeIcon.FolderOpen.ToIconString()}##OpenExportFolder")) OpenLastExportFolder();
        if (lastExportPath is null) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(lastExportPath is null ? "Export a deck first" : "Open the exported deck's folder");
        ImGui.TextDisabled($"Your live deck files are stored in: {store.DecksDirectory}");
    }

    private void DrawRelayGameTab()
    {
        ImGui.Spacing();
        SectionHeading("Relay Multiplayer");
        ImGui.TextWrapped("Create or join an online room without exposing player IP addresses. The host's selected deck and custom artwork are synchronized once and locked for everyone in the room.");
        ImGui.TextColored(new Vector4(.95f, .78f, .30f, 1f), "Beta feature — relay rooms may change while 1.4.0 is being tested.");

        if (ImGui.CollapsingHeader("Relay connection settings"))
        {
            ImGui.SetNextItemWidth(MathF.Min(520 * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X));
            if (ImGui.InputText("Relay address", ref relayEndpoint, 512)) { }
            ImGui.SameLine();
            if (ImGui.Button("Save##RelayEndpoint"))
            {
                TryAction(() => relayGame.SetEndpoint(relayEndpoint));
                configuration.RelayEndpoint = relayEndpoint.Trim().TrimEnd('/');
                saveConfiguration(configuration);
            }
            ImGui.SameLine();
            if (ImGui.Button("Check Relay") && relayTask is null)
                relayTask = relayGame.CheckHealthAsync();
            ImGui.TextDisabled("Official beta endpoint: https://levemetes-relay.kaerlath.workers.dev");
        }

        var characterLabel = GetLocalCharacterLabel();
        ImGui.TextUnformatted("Playing as");
        ImGui.SameLine();
        if (characterLabel is null) ImGui.TextColored(new Vector4(1f, .35f, .35f, 1f), "Log in to a character to use relay multiplayer");
        else ImGui.TextColored(new Vector4(.45f, .9f, .55f, 1f), characterLabel);

        if (relayGame.IsConnected)
        {
            var room = relayGame.Room;
            ImGui.Separator();
            ImGui.TextColored(new Vector4(.45f, .9f, .55f, 1f), relayGame.IsHost ? "Hosting Relay Room" : "Connected to Relay Room");
            if (room is not null)
            {
                ImGui.TextUnformatted($"{room.Name}  •  Code: {room.Code}");
                if (room.Visibility.Equals("private", StringComparison.OrdinalIgnoreCase))
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Copy Code##RelayRoomCode"))
                    {
                        ImGui.SetClipboardText(room.Code);
                        SetStatus("Private relay room code copied to the clipboard.");
                    }
                }
                ImGui.TextDisabled($"Host: {room.Host}  •  {room.Players}/{room.Capacity} players  •  {string.Join(", ", room.Intensity)}");
            }
            FormSectionHeading($"PLAYERS ({relayGame.Players.Count}/16)");
            foreach (var player in relayGame.Players)
            {
                ImGui.Bullet(); ImGui.SameLine();
                ImGui.TextColored(player.Connected ? Vector4.One : new Vector4(.6f, .6f, .6f, 1f),
                    player.Name + (player.Host ? " (Host)" : string.Empty) + (player.Connected ? string.Empty : " (reconnecting)"));
                if (relayGame.IsHost && !player.Host)
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton($"Remove##Relay{player.Id}")) relayTask = relayGame.RemovePlayerAsync(player.Name);
                }
            }
            if (relayGame.IsHost && relayGame.Game?.Started != true)
            {
                ImGui.Spacing();
                if (ImGui.Button("Start Game", new Vector2(180, 36) * ImGuiHelpers.GlobalScale))
                    relayTask = relayGame.StartGameAsync();
                ImGui.SameLine(); ImGui.TextDisabled("Randomizes the first player and synchronized turn order.");
            }
            else if (relayGame.Game?.Started == true)
                ImGui.TextUnformatted($"Current turn: {relayGame.Game.CurrentPlayer}");
            ImGui.Spacing();
            if (ImGui.Button(relayGame.IsHost ? "Close Relay Room" : "Leave Relay Room") && relayTask is null)
                relayTask = relayGame.IsHost ? relayGame.CloseRoomAsync() : relayGame.LeaveAsync();
            return;
        }

        var waiting = relayTask is not null || relayGame.IsBusy;
        if (waiting) ImGui.TextDisabled("Contacting the relay…");

        FormSectionHeading("PUBLIC ROOMS");
        if (waiting) ImGui.BeginDisabled();
        if (ImGui.Button("Refresh Public Rooms")) relayTask = relayGame.RefreshRoomsAsync();
        if (waiting) ImGui.EndDisabled();
        if (relayGame.PublicRooms.Count == 0) ImGui.TextDisabled("No public rooms loaded. Select Refresh Public Rooms.");
        foreach (var room in relayGame.PublicRooms)
        {
            ImGui.PushID(room.Code);
            ImGui.TextUnformatted(room.Name);
            ImGui.SameLine();
            ImGui.TextDisabled($"{room.Players}/{room.Capacity} • {string.Join(", ", room.Intensity)}{(room.PasswordProtected ? " • Password" : string.Empty)}");
            ImGui.SameLine();
            if (ImGui.SmallButton("Select")) relayRoomCode = room.Code;
            ImGui.PopID();
        }

        FormSectionHeading("CREATE A RELAY ROOM");
        ImGui.SetNextItemWidth(360 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("Room name##Relay", "Room name", ref relayRoomName, 80);
        ImGui.Checkbox("Private room (join by code)", ref relayPrivateRoom);
        ImGui.SameLine();
        ImGui.Checkbox("Enable scoring (0–5)", ref relayScoringEnabled);
        ImGui.SetNextItemWidth(300 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("Optional password##RelayCreate", "Leave blank for no password", ref relayPassword, 128, ImGuiInputTextFlags.Password);
        ImGui.TextWrapped($"Selected deck: {selectedDeck.Name}  •  Intensity: {CategoryLabel(playCategory)}");
        if (waiting || characterLabel is null || string.IsNullOrWhiteSpace(relayRoomName)) ImGui.BeginDisabled();
        if (ImGui.Button("Create Relay Room", new Vector2(190, 36) * ImGuiHelpers.GlobalScale))
        {
            try
            {
                relayGame.SetEndpoint(relayEndpoint);
                var bundle = store.ExportBundleBytes(selectedDeck);
                var relayCards = selectedDeck.Cards.Where(card => card.Category.HasFlag(playCategory))
                    .Select(card => new RelayCardDefinition(card.Id, card.Keyword?.ToString() ?? string.Empty)).ToArray();
                relayTask = relayGame.CreateRoomAsync(relayRoomName, characterLabel!, relayPrivateRoom, relayPassword,
                    [CategoryLabel(playCategory)], bundle, relayCards, relayScoringEnabled);
            }
            catch (Exception ex) { SetStatus(ex.Message, true); }
        }
        if (waiting || characterLabel is null || string.IsNullOrWhiteSpace(relayRoomName)) ImGui.EndDisabled();

        FormSectionHeading("JOIN A RELAY ROOM");
        ImGui.SetNextItemWidth(180 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("Room code##RelayJoin", "Eight-character code", ref relayRoomCode, 8);
        ImGui.SetNextItemWidth(300 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("Password##RelayJoin", "Only if the room requires one", ref relayPassword, 128, ImGuiInputTextFlags.Password);
        ImGui.TextDisabled("Joining downloads and verifies the host's deck and custom artwork before play.");
        if (waiting || characterLabel is null || relayRoomCode.Trim().Length != 8) ImGui.BeginDisabled();
        if (ImGui.Button("Join Relay Room", new Vector2(190, 36) * ImGuiHelpers.GlobalScale))
        {
            relayGame.SetEndpoint(relayEndpoint);
            var reconnect = configuration.RelayReconnectRoomCode.Equals(relayRoomCode.Trim(), StringComparison.OrdinalIgnoreCase)
                ? UnprotectReconnectToken(configuration.RelayReconnectToken) : string.Empty;
            relayTask = relayGame.JoinRoomAsync(relayRoomCode, characterLabel!, relayPassword, reconnect);
        }
        if (waiting || characterLabel is null || relayRoomCode.Trim().Length != 8) ImGui.EndDisabled();
    }

    private void ProcessRelayGameEvents()
    {
        if (relayTask is { IsCompleted: true } completedTask)
        {
            relayTask = null;
            try { completedTask.GetAwaiter().GetResult(); }
            catch (Exception ex) { Plugin.Log.Warning(ex, "Relay multiplayer command failed"); SetStatus(ex.Message, true); }
        }
        while (relayGame.TryDequeue(out var gameEvent))
        {
            try
            {
                switch (gameEvent.Type)
                {
                    case RelayGameEventType.DeckReceived:
                        if (gameEvent.Bundle is null) throw new InvalidDataException("The relay returned an empty deck bundle.");
                        var synchronizedDeck = store.ImportBundleBytes(gameEvent.Bundle);
                        decks.Add(synchronizedDeck);
                        SelectDeck(synchronizedDeck);
                        if (gameEvent.Room?.Intensity.FirstOrDefault() is string intensity)
                            playCategory = BasicCategories.FirstOrDefault(category => CategoryLabel(category).Equals(intensity, StringComparison.OrdinalIgnoreCase));
                        configuration.RelayReconnectRoomCode = gameEvent.Room?.Code ?? string.Empty;
                        configuration.RelayReconnectToken = ProtectReconnectToken(relayGame.ReconnectToken);
                        saveConfiguration(configuration);
                        SetStatus(gameEvent.Message);
                        break;
                    case RelayGameEventType.RoomJoined:
                        configuration.RelayReconnectRoomCode = gameEvent.Room?.Code ?? string.Empty;
                        configuration.RelayReconnectToken = ProtectReconnectToken(relayGame.ReconnectToken);
                        saveConfiguration(configuration);
                        SetStatus(gameEvent.Message);
                        break;
                    case RelayGameEventType.CardDrawn:
                        directCurrentCard = selectedDeck.Cards.FirstOrDefault(card => card.Id == gameEvent.CardId)
                            ?? throw new InvalidDataException("The relay draw referred to a missing synchronized card.");
                        directDrawer = gameEvent.Drawer ?? "A player";
                        networkDrawRequested = false;
                        networkVolunteerPending = directCurrentCard.Keyword == CardKeyword.BlindVolunteer;
                        SetStatus($"{directDrawer} drew a card.");
                        break;
                    case RelayGameEventType.Reset:
                        directCurrentCard = null; directDrawer = string.Empty;
                        networkDrawRequested = false; networkVolunteerPending = false;
                        SetStatus(gameEvent.Message); break;
                    case RelayGameEventType.GameStarted:
                        directCurrentCard = null; directDrawer = string.Empty;
                        networkDrawRequested = false; networkVolunteerPending = false;
                        requestPlayTab = true; SetStatus(gameEvent.Message); break;
                    case RelayGameEventType.GameStateChanged:
                        var relayState = relayGame.Game;
                        networkVolunteerPending = relayState?.PendingVolunteer is not null;
                        if (relayState?.CurrentCardId is Guid currentRelayCard)
                        {
                            directCurrentCard = selectedDeck.Cards.FirstOrDefault(card => card.Id == currentRelayCard);
                            directDrawer = relayState.CurrentDrawer ?? directDrawer;
                        }
                        if (relayState?.PendingVolunteer is { } pending &&
                            !pending.Drawer.Equals(GetLocalCharacterLabel(), StringComparison.OrdinalIgnoreCase))
                        {
                            volunteerResolutionId = pending.ResolutionId;
                            volunteerDeadlineUnixMilliseconds = pending.Deadline;
                            volunteerDrawer = pending.Drawer;
                        }
                        if (relayState?.Started == true) requestPlayTab = true;
                        break;
                    case RelayGameEventType.VolunteerPrompt:
                        volunteerResolutionId = gameEvent.ResolutionId;
                        volunteerDeadlineUnixMilliseconds = gameEvent.DeadlineUnixMilliseconds;
                        volunteerDrawer = gameEvent.Drawer ?? "A player";
                        ImGui.OpenPopup("Blind Volunteer Needed");
                        SetStatus(gameEvent.Message);
                        break;
                    case RelayGameEventType.VolunteerResolved:
                        networkVolunteerPending = false;
                        volunteerResolutionId = null; volunteerDeadlineUnixMilliseconds = 0; volunteerDrawer = string.Empty;
                        if (gameEvent.CardId is Guid relayCardId)
                            directCurrentCard = selectedDeck.Cards.FirstOrDefault(card => card.Id == relayCardId)
                                ?? throw new InvalidDataException("The resolved relay card is missing from the synchronized deck.");
                        directDrawer = gameEvent.Drawer ?? directDrawer;
                        SetStatus(gameEvent.Message);
                        break;
                    case RelayGameEventType.RandomTargetSelected: SetStatus(gameEvent.Message); break;
                    case RelayGameEventType.GameEnded:
                        networkDrawRequested = false; networkVolunteerPending = false;
                        finalScores = gameEvent.Scores ?? new Dictionary<string, int>();
                        finalWinners = gameEvent.Winners;
                        ImGui.OpenPopup("Game Results");
                        SetStatus(gameEvent.Message);
                        break;
                    case RelayGameEventType.RoomClosed:
                        directCurrentCard = null; directDrawer = string.Empty;
                        networkDrawRequested = false; networkVolunteerPending = false;
                        configuration.RelayReconnectRoomCode = string.Empty;
                        configuration.RelayReconnectToken = string.Empty;
                        saveConfiguration(configuration);
                        _ = relayGame.LeaveAsync();
                        SetStatus(gameEvent.Message);
                        break;
                    case RelayGameEventType.Error: networkDrawRequested = false; SetStatus(gameEvent.Message, true); break;
                    default: SetStatus(gameEvent.Message); break;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Could not process a Relay Multiplayer event");
                SetStatus(ex.Message, true);
                _ = relayGame.LeaveAsync();
            }
        }
    }

    private void DrawDirectGameTab()
    {
        ProcessPublicAddressDiscovery();
        ImGui.Spacing();
        SectionHeading("Direct Private Game");
        var enabled = configuration.EnableExperimentalDirectPlay;
        if (directGame.IsConnected) ImGui.BeginDisabled();
        if (ImGui.Checkbox("Enable Experimental Direct Private Game", ref enabled))
        {
            configuration.EnableExperimentalDirectPlay = enabled;
            saveConfiguration(configuration);
        }
        if (directGame.IsConnected) ImGui.EndDisabled();
        ImGui.TextColored(new Vector4(1f, .68f, .28f, 1f),
            "Direct connections expose the host's IP address to guests and guest IP addresses to the host.");
        ImGui.TextWrapped("Use this only with people you trust. Levemetes encrypts and authenticates game messages, but encryption cannot hide the addresses needed to make a direct connection.");
        if (ImGui.Button($"{FontAwesomeIcon.QuestionCircle.ToIconString()} Port Forwarding Help"))
            ImGui.OpenPopup("Direct Private Game Help");
        DrawDirectGameHelpPopup();
        ImGui.Separator();

        if (!enabled)
        {
            ImGui.TextDisabled("Enable the experimental option above to create or join a direct room. Local play is unchanged.");
            return;
        }

        if (!directGame.IsConnected && directGame.Mode != DirectGameMode.Connecting)
        {
            if (!publicAddressDiscoveryAttempted && IsLoopbackOrEmpty(directPublicAddress))
            {
                publicAddressDiscoveryAttempted = true;
                DiscoverPublicAddress();
            }
            var characterLabel = GetLocalCharacterLabel();
            ImGui.TextUnformatted("Playing as");
            ImGui.SameLine();
            if (characterLabel is null)
                ImGui.TextColored(new Vector4(1f, .35f, .35f, 1f), "Log in to a character to use Direct Private Game");
            else
                ImGui.TextColored(new Vector4(.45f, .9f, .55f, 1f), characterLabel);

            FormSectionHeading("CREATE A DIRECT ROOM");
            ImGui.TextWrapped($"The currently selected deck and {CategoryLabel(playCategory)} draw pile will be locked and sent once to each joining player.");
            ImGui.SetNextItemWidth(300 * ImGuiHelpers.GlobalScale);
            ImGui.InputTextWithHint("Public address##DirectHost", "Public IP address or DNS name", ref directPublicAddress, 256);
            ImGui.SameLine();
            if (publicAddressDiscoveryTask is not null) ImGui.BeginDisabled();
            if (ImGui.Button(publicAddressDiscoveryTask is not null ? "Detecting..." : "Detect Public IP")) DiscoverPublicAddress();
            if (publicAddressDiscoveryTask is not null) ImGui.EndDisabled();
            ImGui.TextDisabled("Detection contacts api.ipify.org over HTTPS. You may still enter an address manually.");
            ImGui.Checkbox("Enable optional scoring (0–5)", ref directScoringEnabled);
            ImGui.SetNextItemWidth(140 * ImGuiHelpers.GlobalScale);
            ImGui.InputInt("Listening port##DirectPort", ref directPort);
            if (characterLabel is null) ImGui.BeginDisabled();
            if (ImGui.Button("Create Direct Room")) CreateDirectRoom(characterLabel!);
            if (characterLabel is null) ImGui.EndDisabled();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("May require Windows Firewall permission and router port forwarding.");

            FormSectionHeading("JOIN A DIRECT ROOM");
            ImGui.TextWrapped("Joining downloads the host's custom deck and artwork. Confirm that you trust the host and accept the room's possible content before joining.");
            ImGui.InputTextMultiline("##DirectInvitation", ref directInvitation, 2048,
                new Vector2(-1, 70 * ImGuiHelpers.GlobalScale));
            if (characterLabel is null) ImGui.BeginDisabled();
            if (ImGui.Button("Join with Invitation")) JoinDirectRoom(characterLabel!);
            if (characterLabel is null) ImGui.EndDisabled();
            return;
        }

        if (directGame.Mode == DirectGameMode.Connecting)
        {
            ImGui.TextUnformatted("Connecting to the direct host…");
            if (ImGui.Button("Cancel Connection")) directGame.Stop();
            return;
        }

        ImGui.TextColored(new Vector4(.45f, .9f, .55f, 1f),
            directGame.IsHost ? "Hosting Direct Private Game" : "Connected to Direct Private Game");
        ImGui.TextUnformatted($"Locked deck: {selectedDeck.Name}");
        if (!string.IsNullOrWhiteSpace(selectedDeck.Author)) ImGui.TextDisabled($"by {selectedDeck.Author}");
        ImGui.TextUnformatted($"Intensity: {CategoryLabel(directGame.Category)}");
        ImGui.TextUnformatted($"Shared cards remaining: {directGame.Remaining}");

        if (directGame.IsHost)
        {
            FormSectionHeading("PRIVATE INVITATION");
            var invite = directGame.InviteText;
            ImGui.InputTextMultiline("##HostInvitation", ref invite, 2048,
                new Vector2(-1, 70 * ImGuiHelpers.GlobalScale));
            if (ImGui.Button("Copy Invitation"))
            {
                ImGui.SetClipboardText(directGame.InviteText);
                SetStatus("Direct-room invitation copied. Share it only with trusted players.");
            }
            ImGui.TextDisabled("The invitation contains a secret connection key. Anyone who has it may attempt to join while the room is open.");
        }

        FormSectionHeading($"PLAYERS ({directGame.Players.Count}/8)");
        foreach (var name in directGame.Players)
        {
            ImGui.Bullet();
            ImGui.SameLine();
            ImGui.TextUnformatted(name);
        }
        if (!directGame.GameStarted && directGame.IsHost)
        {
            ImGui.Spacing();
            if (ImGui.Button("Start Game", new Vector2(180, 36) * ImGuiHelpers.GlobalScale))
                TryAction(directGame.StartGame);
            ImGui.SameLine();
            ImGui.TextDisabled("Randomizes the first player and turn order.");
        }
        if (directGame.GameStarted)
        {
            ImGui.Spacing();
            ImGui.TextUnformatted($"Current turn: {directGame.CurrentPlayer}");
            ImGui.TextUnformatted("Turn order");
            foreach (var name in directGame.TurnOrder)
            {
                var connected = directGame.Players.Any(item => RemoveHostSuffix(item).Equals(name, StringComparison.OrdinalIgnoreCase));
                ImGui.Bullet();
                ImGui.SameLine();
                ImGui.TextColored(connected ? Vector4.One : new Vector4(.6f, .6f, .6f, 1f),
                    name + (connected ? string.Empty : " (disconnected)"));
                if (directGame.IsHost && !name.Equals(GetLocalCharacterLabel(), StringComparison.OrdinalIgnoreCase))
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton($"Remove##{name}")) TryAction(() => directGame.RemovePlayer(name));
                }
            }
        }
        if (!string.IsNullOrWhiteSpace(directDrawer)) ImGui.TextDisabled($"Most recent draw: {directDrawer}");
        ImGui.Spacing();
        if (ImGui.Button(directGame.IsHost ? "Close Room" : "Leave Room")) LeaveDirectRoom();
    }

    private void CreateDirectRoom(string characterLabel)
    {
        SaveDirectSettings();
        TryAction(() =>
        {
            var bundle = store.ExportBundleBytes(selectedDeck);
            directGame.StartHosting(characterLabel, directPublicAddress, directPort, selectedDeck, playCategory, bundle, directScoringEnabled);
            directCurrentCard = null;
            directDrawer = string.Empty;
            SetStatus("Direct room created. You may need to allow Levemetes through Windows Firewall and forward the listening port on your router.");
        });
    }

    private void JoinDirectRoom(string characterLabel)
    {
        SaveDirectSettings();
        TryAction(() => directGame.Join(characterLabel, directInvitation));
    }

    private void LeaveDirectRoom()
    {
        directGame.Stop();
        directCurrentCard = null;
        directDrawer = string.Empty;
        session.Reset(selectedDeck, playCategory);
        SetStatus("Direct Private Game ended. Local play is available again.");
    }

    private void SaveDirectSettings()
    {
        configuration.DirectPublicAddress = directPublicAddress.Trim();
        configuration.DirectPort = directPort;
        saveConfiguration(configuration);
    }

    private void DiscoverPublicAddress()
    {
        if (publicAddressDiscoveryTask is not null) return;
        publicAddressDiscoveryTask = DirectGameService.DiscoverPublicAddressAsync();
    }

    private void ProcessPublicAddressDiscovery()
    {
        if (publicAddressDiscoveryTask is not { IsCompleted: true } task) return;
        publicAddressDiscoveryTask = null;
        try
        {
            directPublicAddress = task.GetAwaiter().GetResult();
            SaveDirectSettings();
            SetStatus("Public IPv4 address detected. Router port forwarding may still be required.");
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not detect the public IPv4 address");
            SetStatus("Public IP detection failed. Enter the host address manually.", true);
        }
    }

    private static bool IsLoopbackOrEmpty(string value) => string.IsNullOrWhiteSpace(value) ||
        (IPAddress.TryParse(value, out var address) && IPAddress.IsLoopback(address));

    private static string RemoveHostSuffix(string value) => value.EndsWith(" (Host)", StringComparison.Ordinal)
        ? value[..^7]
        : value;

    private static string ProtectReconnectToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    }

    private static string UnprotectReconnectToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser)); }
        catch { return string.Empty; }
    }

    private static string? GetLocalCharacterLabel()
    {
        if (!Plugin.PlayerState.IsLoaded) return null;
        var characterName = Plugin.PlayerState.CharacterName.Trim();
        var homeWorld = Plugin.PlayerState.HomeWorld.Value.Name.ToString().Trim();
        if (string.IsNullOrWhiteSpace(characterName) || string.IsNullOrWhiteSpace(homeWorld)) return null;
        return $"{characterName} @ {homeWorld}";
    }

    private void ProcessDirectGameEvents()
    {
        while (directGame.TryDequeue(out var gameEvent))
        {
            try
            {
                switch (gameEvent.Type)
                {
                    case DirectGameEventType.DeckReceived:
                        if (gameEvent.Bundle is null) throw new InvalidDataException("The host sent an empty deck bundle.");
                        var synchronizedDeck = store.ImportBundleBytes(gameEvent.Bundle);
                        decks.Add(synchronizedDeck);
                        playCategory = gameEvent.Category;
                        configuration.SelectedCategory = playCategory;
                        SelectDeck(synchronizedDeck);
                        directCurrentCard = null;
                        SetStatus($"Synchronized host deck “{synchronizedDeck.Name}” and joined Direct Private Game.");
                        break;
                    case DirectGameEventType.CardDrawn:
                        directCurrentCard = selectedDeck.Cards.FirstOrDefault(card => card.Id == gameEvent.CardId)
                            ?? throw new InvalidDataException("The shared draw referred to a card that is missing from the synchronized deck.");
                        directDrawer = gameEvent.Drawer ?? "A player";
                        networkDrawRequested = false;
                        networkVolunteerPending = directCurrentCard.Keyword == CardKeyword.BlindVolunteer;
                        SetStatus(gameEvent.Message);
                        break;
                    case DirectGameEventType.Reset:
                        directCurrentCard = null;
                        directDrawer = string.Empty;
                        networkDrawRequested = false;
                        networkVolunteerPending = false;
                        SetStatus(gameEvent.Message);
                        break;
                    case DirectGameEventType.Error:
                        networkDrawRequested = false;
                        SetStatus(gameEvent.Message, true);
                        break;
                    case DirectGameEventType.Status:
                        SetStatus(gameEvent.Message);
                        break;
                    case DirectGameEventType.GameStarted:
                        directCurrentCard = null;
                        directDrawer = string.Empty;
                        networkDrawRequested = false;
                        networkVolunteerPending = false;
                        requestPlayTab = true;
                        SetStatus(gameEvent.Message);
                        break;
                    case DirectGameEventType.GameStateChanged:
                        SetStatus(gameEvent.Message);
                        break;
                    case DirectGameEventType.VolunteerPrompt:
                        volunteerResolutionId = gameEvent.ResolutionId;
                        volunteerDeadlineUnixMilliseconds = gameEvent.DeadlineUnixMilliseconds;
                        volunteerDrawer = gameEvent.Drawer ?? "A player";
                        ImGui.OpenPopup("Blind Volunteer Needed");
                        SetStatus(gameEvent.Message);
                        break;
                    case DirectGameEventType.VolunteerResolved:
                        networkVolunteerPending = false;
                        volunteerResolutionId = null;
                        volunteerDeadlineUnixMilliseconds = 0;
                        volunteerDrawer = string.Empty;
                        if (gameEvent.CardId is Guid revealedCardId)
                            directCurrentCard = selectedDeck.Cards.FirstOrDefault(card => card.Id == revealedCardId)
                                ?? throw new InvalidDataException("The resolved BLIND VOLUNTEER card is missing from the synchronized deck.");
                        directDrawer = gameEvent.Drawer ?? directDrawer;
                        SetStatus(gameEvent.Message);
                        break;
                    case DirectGameEventType.RandomTargetSelected:
                        SetStatus(gameEvent.Message);
                        break;
                    case DirectGameEventType.ScoreStateChanged:
                        SetStatus(gameEvent.Message);
                        break;
                    case DirectGameEventType.TieBreakStarted:
                        SetStatus(gameEvent.Message);
                        break;
                    case DirectGameEventType.GameEnded:
                        networkDrawRequested = false;
                        networkVolunteerPending = false;
                        finalScores = gameEvent.Scores ?? new Dictionary<string, int>();
                        finalWinners = gameEvent.Winners;
                        ImGui.OpenPopup("Game Results");
                        SetStatus(gameEvent.Message);
                        break;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Could not process a Direct Private Game event");
                SetStatus(ex.Message, true);
                directGame.Stop();
            }
        }
    }

    private void DrawGameResultsPopup()
    {
        if (!ImGui.BeginPopupModal("Game Results", ImGuiWindowFlags.AlwaysAutoResize)) return;
        var results = finalScores?.OrderByDescending(pair => pair.Value).ToArray() ?? [];
        if (results.Length == 0) ImGui.TextUnformatted("Game ended. Scoring was not enabled.");
        else
        {
            var best = results[0].Value;
            var winners = finalWinners?.ToArray() ?? results.Where(pair => pair.Value == best).Select(pair => pair.Key).ToArray();
            ImGui.TextColored(ThemeGoldBright, winners.Length == 1
                ? $"Winner: {winners[0]} — {best} points"
                : $"Shared victory: {string.Join(" & ", winners)} — {best} points each");
            GoldSeparator();
            foreach (var pair in results) ImGui.TextUnformatted($"{pair.Key}: {pair.Value}");
        }
        if (ImGui.Button("Close")) { ImGui.CloseCurrentPopup(); finalScores = null; finalWinners = null; }
        ImGui.EndPopup();
    }

    private void DrawStatus()
    {
        if (string.IsNullOrWhiteSpace(status)) return;
        ImGui.Separator();
        ImGui.TextColored(statusIsError ? new Vector4(1f, .35f, .35f, 1f) : new Vector4(.45f, .9f, .55f, 1f), status);
    }

    private void DrawPlayStatus()
    {
        statusRenderedThisFrame = true;
        if (string.IsNullOrWhiteSpace(status)) return;
        ImGui.Spacing();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        ImGui.TextColored(statusIsError ? new Vector4(1f, .35f, .35f, 1f) : new Vector4(.45f, .9f, .55f, 1f), status);
        ImGui.PopTextWrapPos();
    }

    private void DrawVolunteerPrompt()
    {
        if (volunteerResolutionId is not null) ImGui.OpenPopup("Blind Volunteer Needed");
        ImGui.SetNextWindowSize(new Vector2(430, 270) * ImGuiHelpers.GlobalScale, ImGuiCond.Appearing);
        if (!ImGui.BeginPopupModal("Blind Volunteer Needed", ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings)) return;
        if (volunteerResolutionId is not Guid resolutionId)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        var remainingMilliseconds = Math.Max(0, volunteerDeadlineUnixMilliseconds - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var remainingSeconds = (int)Math.Ceiling(remainingMilliseconds / 1000d);
        ImGui.TextWrapped($"{volunteerDrawer} drew a BLIND VOLUNTEER card. The card itself is hidden from everyone else.");
        ImGui.Spacing();
        ImGui.TextWrapped("For the first five seconds, everyone who volunteers enters a random selection pool. After that, the first volunteer is chosen. If no one volunteers before time expires, a connected player is chosen at random.");
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(.95f, .78f, .30f, 1f), $"Time remaining: {remainingSeconds} seconds");
        ImGui.Spacing();
        var buttonSize = new Vector2(190, 40) * ImGuiHelpers.GlobalScale;
        ImGui.SetCursorPosX((ImGui.GetWindowSize().X - buttonSize.X) / 2);
        if (ImGui.Button("I VOLUNTEER", buttonSize))
        {
            if (relayGame.IsConnected) relayTask = relayGame.VolunteerAsync(resolutionId);
            else directGame.Volunteer(resolutionId);
            volunteerResolutionId = null;
            SetStatus("Volunteer request sent.");
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private void DrawDirectGameHelpPopup()
    {
        ImGui.SetNextWindowSize(new Vector2(620, 520) * ImGuiHelpers.GlobalScale, ImGuiCond.FirstUseEver);
        if (!ImGui.BeginPopupModal("Direct Private Game Help", ImGuiWindowFlags.NoSavedSettings)) return;
        try
        {
            if (File.Exists(directGameHelpPath))
                ImGui.TextWrapped(File.ReadAllText(directGameHelpPath));
            else
                ImGui.TextWrapped("The help file could not be found. Forward TCP port 43871 to this computer's local IPv4 address in your router's Port Forwarding or NAT settings.");
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not read Direct Private Game help");
            ImGui.TextWrapped("The help file could not be read. Forward TCP port 43871 to this computer's local IPv4 address in your router's Port Forwarding or NAT settings.");
        }
        ImGui.Spacing();
        if (ImGui.Button("Close", new Vector2(100, 30) * ImGuiHelpers.GlobalScale)) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void DrawGameInstructionsButton()
    {
        var size = new Vector2(34, 34) * ImGuiHelpers.GlobalScale;
        var padding = ImGui.GetStyle().WindowPadding;
        var windowSize = ImGui.GetWindowSize();
        ImGui.SetCursorPos(new Vector2(
            MathF.Max(padding.X, windowSize.X - padding.X - size.X),
            MathF.Max(ImGui.GetCursorPosY(), windowSize.Y - padding.Y - size.Y)));
        if (ImGui.Button($"{FontAwesomeIcon.QuestionCircle.ToIconString()}##GameInstructions", size))
            ImGui.OpenPopup("How to Play Levemetes");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("How to play Levemetes");
        DrawGameInstructionsPopup();
    }

    private void DrawGameInstructionsPopup()
    {
        ImGui.SetNextWindowSize(new Vector2(580, 500) * ImGuiHelpers.GlobalScale, ImGuiCond.FirstUseEver);
        if (!ImGui.BeginPopupModal("How to Play Levemetes", ImGuiWindowFlags.NoSavedSettings)) return;
        try
        {
            if (File.Exists(gameInstructionsPath))
                ImGui.TextWrapped(File.ReadAllText(gameInstructionsPath));
            else
                ImGui.TextWrapped("The game instructions file could not be found. Choose a deck and intensity, draw a card, and use Shuffle / Reset when you want to refill the draw pile.");
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not read Levemetes game instructions");
            ImGui.TextWrapped("The game instructions file could not be read.");
        }
        ImGui.Spacing();
        if (ImGui.Button("Close", new Vector2(100, 30) * ImGuiHelpers.GlobalScale)) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
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
            card.Artwork = artworkChoice;
            card.CustomArtworkId = customArtworkId;
            card.Category = cardCategory;
            card.Keyword = cardKeyword;
            card.Text = text;
            card.FlavorText = flavorText.Trim();
        }
        else selectedDeck.Cards.Add(new Card
        {
            Title = cardTitle.Trim(), Activity = activityType, Artwork = artworkChoice,
            CustomArtworkId = customArtworkId,
            Category = cardCategory, Keyword = cardKeyword, Text = text,
            FlavorText = flavorText.Trim(),
        });
        SaveDeck(editingCardId is null ? "Card added." : "Card updated.");
        ClearEditor();
    }

    private void BeginEdit(Card card)
    {
        editingCardId = card.Id;
        cardTitle = card.Title;
        activityType = card.Activity;
        artworkChoice = card.Artwork;
        customArtworkId = card.CustomArtworkId;
        cardCategory = card.Category;
        cardKeyword = card.Keyword;
        cardText = card.Text;
        flavorText = card.FlavorText;
    }

    private void ClearEditor()
    {
        editingCardId = null;
        cardTitle = string.Empty;
        activityType = ActivityType.ActionSelf;
        artworkChoice = ArtworkChoice.SfwAdventurersResolve;
        customArtworkId = null;
        cardCategory = CardCategory.Sfw;
        cardKeyword = null;
        cardText = string.Empty;
        flavorText = string.Empty;
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

    private void OpenImportDialog(bool merge)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        fileDialogManager.OpenFileDialog(
            merge ? $"Merge a deck into {selectedDeck.Name}" : "Import a Levemetes deck",
            ".levemetesdeck,.json",
            (success, paths) =>
            {
                if (!success) return;
                TryAction(() => ImportDeck(paths[0], merge));
            },
            1,
            desktop,
            true);
    }

    private void OpenArtworkDialog()
    {
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        fileDialogManager.OpenFileDialog(
            "Add custom card artwork",
            ".png,.jpg,.jpeg,.bmp,.gif",
            (success, paths) =>
            {
                if (!success) return;
                TryAction(() =>
                {
                    var asset = store.AddCustomArtwork(selectedDeck, paths[0]);
                    customArtworkId = asset.Id;
                    SetStatus($"Added custom artwork ‘{asset.Name}’. It was automatically cropped and resized.");
                });
            },
            1,
            pictures,
            true);
    }

    private void OpenCardBackDialog()
    {
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        fileDialogManager.OpenFileDialog(
            "Choose a custom deck card back",
            ".png,.jpg,.jpeg,.bmp,.gif",
            (success, paths) =>
            {
                if (!success) return;
                TryAction(() =>
                {
                    store.SetCustomCardBack(selectedDeck, paths[0]);
                    SetStatus("Custom card back added. The image was center-cropped and resized automatically.");
                });
            },
            1,
            pictures,
            true);
    }

    private string CurrentCardBackPath() => store.GetCustomCardBackPath(selectedDeck) ?? cardBackPath;

    private void OpenExportDialog()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        fileDialogManager.SaveFileDialog(
            "Export Levemetes deck",
            ".levemetesdeck",
            SanitizeFileName(selectedDeck.Name),
            ".levemetesdeck",
            (success, path) =>
            {
                if (!success) return;
                TryAction(() =>
                {
                    lastExportPath = store.Export(selectedDeck, path);
                    SetStatus($"Exported to {lastExportPath}");
                });
            },
            desktop,
            true);
    }

    private void OpenLastExportFolder()
    {
        if (lastExportPath is null) return;
        TryAction(() =>
        {
            var folder = Path.GetDirectoryName(lastExportPath);
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                throw new DirectoryNotFoundException("The export folder no longer exists.");
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        });
    }

    private void ImportDeck(string path, bool merge)
    {
        if (merge)
        {
            mergePreview = store.PreviewMerge(path, selectedDeck);
            mergeSelectedCards.Clear();
            foreach (var card in mergePreview.Cards.Where(card => !card.IsDuplicate)) mergeSelectedCards.Add(card.SourceIndex);
            requestMergePreviewPopup = true;
            SetStatus($"Reviewing {mergePreview.Cards.Count} cards from {mergePreview.DeckName}.");
            return;
        }

        var deck = store.Import(path);
        decks.Add(deck);
        SelectDeck(deck);
        SetStatus($"Imported {deck.Name} as a new deck.");
    }

    private void DrawMergePreviewPopup()
    {
        if (requestMergePreviewPopup)
        {
            ImGui.OpenPopup("Choose Cards to Merge");
            requestMergePreviewPopup = false;
        }
        ImGui.SetNextWindowSize(new Vector2(760, 620) * ImGuiHelpers.GlobalScale, ImGuiCond.Appearing);
        if (!ImGui.BeginPopupModal("Choose Cards to Merge", ImGuiWindowFlags.NoSavedSettings)) return;
        if (mergePreview is null) { ImGui.CloseCurrentPopup(); ImGui.EndPopup(); return; }

        SectionHeading($"Merge from {mergePreview.DeckName}");
        if (!string.IsNullOrWhiteSpace(mergePreview.Author)) ImGui.TextDisabled($"by {mergePreview.Author}");
        var available = mergePreview.Cards.Count(card => !card.IsDuplicate);
        var duplicateCount = mergePreview.Cards.Count - available;
        ImGui.TextWrapped($"Choose cards to add to {selectedDeck.Name}. Duplicate cards cannot be selected. " +
            $"{available} available; {duplicateCount} duplicate{(duplicateCount == 1 ? string.Empty : "s")} already present.");

        if (ImGui.Button("Select All"))
        {
            mergeSelectedCards.Clear();
            foreach (var card in mergePreview.Cards.Where(card => !card.IsDuplicate)) mergeSelectedCards.Add(card.SourceIndex);
        }
        ImGui.SameLine();
        if (ImGui.Button("Clear All")) mergeSelectedCards.Clear();
        ImGui.SameLine();
        ImGui.TextDisabled($"{mergeSelectedCards.Count} selected");
        GoldSeparator();

        if (ImGui.BeginChild("##MergeCardList", new Vector2(0, -58 * ImGuiHelpers.GlobalScale), true))
        {
            foreach (var card in mergePreview.Cards)
            {
                ImGui.PushID(card.SourceIndex);
                var selected = mergeSelectedCards.Contains(card.SourceIndex);
                if (card.IsDuplicate) ImGui.BeginDisabled();
                if (ImGui.Checkbox("##SelectMergeCard", ref selected))
                {
                    if (selected) mergeSelectedCards.Add(card.SourceIndex);
                    else mergeSelectedCards.Remove(card.SourceIndex);
                }
                if (card.IsDuplicate) ImGui.EndDisabled();
                ImGui.SameLine();
                ImGui.TextUnformatted(string.IsNullOrWhiteSpace(card.Title) ? "Untitled Levemete" : card.Title);
                ImGui.SameLine();
                ImGui.TextDisabled($"• {ActivityLabel(card.Activity)} • {CategoryLabel(card.Category)}");
                if (card.HasCustomArtwork)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(ThemeGoldBright, $"{FontAwesomeIcon.Image.ToIconString()} Custom image");
                }
                if (card.IsDuplicate)
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled("Already in selected deck");
                }
                var previewText = card.Text.Replace('\r', ' ').Replace('\n', ' ').Trim();
                if (previewText.Length > 180) previewText = previewText[..177] + "...";
                if (!string.IsNullOrWhiteSpace(previewText))
                {
                    ImGui.Indent(30 * ImGuiHelpers.GlobalScale);
                    ImGui.TextWrapped(previewText);
                    ImGui.Unindent(30 * ImGuiHelpers.GlobalScale);
                }
                ImGui.Separator();
                ImGui.PopID();
            }
        }
        ImGui.EndChild();

        if (mergeSelectedCards.Count == 0) ImGui.BeginDisabled();
        if (ImGui.Button("Import Selected", new Vector2(170, 38) * ImGuiHelpers.GlobalScale))
        {
            TryAction(() =>
            {
                var result = store.MergeSelected(mergePreview, selectedDeck, mergeSelectedCards);
                session.Reset(selectedDeck, playCategory);
                SetStatus($"Imported {result.Added} selected card{(result.Added == 1 ? string.Empty : "s")}; skipped {result.Skipped} duplicate{(result.Skipped == 1 ? string.Empty : "s")}.");
                mergePreview = null;
                mergeSelectedCards.Clear();
                ImGui.CloseCurrentPopup();
            });
        }
        if (mergeSelectedCards.Count == 0) ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(110, 38) * ImGuiHelpers.GlobalScale))
        {
            mergePreview = null;
            mergeSelectedCards.Clear();
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private void SelectDeck(Deck deck)
    {
        selectedDeck = deck;
        configuration.SelectedDeckId = deck.Id;
        saveConfiguration(configuration);
        session.Reset(deck, playCategory);
        ClearEditor();
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

        var artworkPath = ResolveArtworkPath(card.CustomArtworkId, card.Artwork);
        var artworkTexture = Plugin.TextureProvider.GetFromFile(artworkPath).GetWrapOrDefault();
        if (artworkTexture is not null)
        {
            var artworkSize = new Vector2(
                MathF.Min(cardSize.X * .88f, 440 * ImGuiHelpers.GlobalScale),
                MathF.Min(cardSize.Y * .315f, 205 * ImGuiHelpers.GlobalScale));
            var artworkPosition = new Vector2((cardSize.X - artworkSize.X) / 2f, cardSize.Y * .205f);
            ImGui.SetCursorPos(artworkPosition);
            ImGui.Image(artworkTexture.Handle, artworkSize, new Vector2(0, .165f), new Vector2(1, .835f));
            var minimum = ImGui.GetWindowPos() + artworkPosition;
            ImGui.GetWindowDrawList().AddRect(minimum, minimum + artworkSize,
                ImGui.ColorConvertFloat4ToU32(new Vector4(.65f, .48f, .18f, 1f)),
                5f * ImGuiHelpers.GlobalScale, ImDrawFlags.None, 2f * ImGuiHelpers.GlobalScale);
        }

        var windowPosition = ImGui.GetWindowPos();
        DrawOrnamentalHeader(card, cardSize);

        var ink = CardInkColor(card.Category);
        DrawCardContentPanel(windowPosition + new Vector2(cardSize.X * .055f, cardSize.Y * .535f),
            new Vector2(cardSize.X * .89f, cardSize.Y * .205f), false);
        DrawCardContentPanel(windowPosition + new Vector2(cardSize.X * .055f, cardSize.Y * .76f),
            new Vector2(cardSize.X * .89f, cardSize.Y * .07f), true);

        if (card.Keyword is CardKeyword keyword)
            DrawKeywordBadge(keyword, cardSize, cardSize.Y * .515f);

        DrawFormattedCardText(card.Text, cardSize, ink);
        if (!string.IsNullOrWhiteSpace(card.FlavorText))
            DrawFlavorText(card.FlavorText, cardSize, ink);
        DrawIntensityFooter(card.Category, cardSize);
    }

    private static void DrawCardContentPanel(Vector2 minimum, Vector2 size, bool inset)
    {
        var drawList = ImGui.GetWindowDrawList();
        var fill = inset
            ? new Vector4(.91f, .86f, .74f, .92f)
            : new Vector4(.95f, .92f, .84f, .88f);
        var border = new Vector4(.56f, .40f, .17f, .92f);
        var rounding = 5 * ImGuiHelpers.GlobalScale;
        drawList.AddRectFilled(minimum, minimum + size, ImGui.ColorConvertFloat4ToU32(fill), rounding);
        drawList.AddRect(minimum, minimum + size, ImGui.ColorConvertFloat4ToU32(border), rounding,
            ImDrawFlags.None, (inset ? 1.4f : 1.8f) * ImGuiHelpers.GlobalScale);
        var inner = 5 * ImGuiHelpers.GlobalScale;
        drawList.AddRect(minimum + new Vector2(inner), minimum + size - new Vector2(inner),
            ImGui.ColorConvertFloat4ToU32(new Vector4(.68f, .54f, .30f, .48f)), rounding,
            ImDrawFlags.None, ImGuiHelpers.GlobalScale);
    }

    private static void DrawOrnamentalHeader(Card card, Vector2 cardSize)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var windowPosition = ImGui.GetWindowPos();
        var drawList = ImGui.GetWindowDrawList();
        var gold = ImGui.ColorConvertFloat4ToU32(new Vector4(.66f, .46f, .18f, .95f));
        var centerX = windowPosition.X + cardSize.X / 2;
        DrawCenteredOverlayTextFitted(card.Title, cardSize.Y * .118f, 10f, cardSize.X * .68f,
            new Vector4(.18f, .10f, .045f, 1f));
        var activity = ActivityLabel(card.Activity);
        var activityScale = .98f;
        var activitySize = ImGui.CalcTextSize(activity) * activityScale;
        var activityY = windowPosition.Y + cardSize.Y * .169f;
        var activityX = centerX - activitySize.X / 2;
        var flourishGap = 10 * scale;
        var flourishLength = cardSize.X * .09f;
        drawList.AddLine(new Vector2(activityX - flourishGap - flourishLength, activityY + activitySize.Y / 2),
            new Vector2(activityX - flourishGap, activityY + activitySize.Y / 2), gold, scale);
        drawList.AddLine(new Vector2(activityX + activitySize.X + flourishGap, activityY + activitySize.Y / 2),
            new Vector2(activityX + activitySize.X + flourishGap + flourishLength, activityY + activitySize.Y / 2), gold, scale);
        var diamond = 3.2f * scale;
        foreach (var x in new[] { activityX - flourishGap, activityX + activitySize.X + flourishGap })
            drawList.AddQuadFilled(new Vector2(x, activityY + activitySize.Y / 2 - diamond),
                new Vector2(x + diamond, activityY + activitySize.Y / 2),
                new Vector2(x, activityY + activitySize.Y / 2 + diamond),
                new Vector2(x - diamond, activityY + activitySize.Y / 2), gold);
        DrawCenteredOverlayTextFitted(activity, cardSize.Y * .163f, 0f, cardSize.X * .55f,
            new Vector4(.32f, .20f, .08f, 1f));
    }

    private static void DrawKeywordBadge(CardKeyword keyword, Vector2 cardSize, float y)
    {
        var label = KeywordLabel(keyword);
        var scale = ImGuiHelpers.GlobalScale;
        var textSize = ImGui.CalcTextSize(label);
        var size = textSize + new Vector2(24, 9) * scale;
        var position = ImGui.GetWindowPos() + new Vector2((cardSize.X - size.X) / 2, y);
        var drawList = ImGui.GetWindowDrawList();
        var fill = keyword switch
        {
            CardKeyword.BlindVolunteer => new Vector4(.20f, .32f, .42f, .98f),
            CardKeyword.Choice => new Vector4(.24f, .38f, .25f, .98f),
            _ => new Vector4(.42f, .18f, .22f, .98f),
        };
        var rounding = 4 * scale;
        drawList.AddRectFilled(position, position + size, ImGui.ColorConvertFloat4ToU32(fill), rounding);
        drawList.AddRect(position, position + size,
            ImGui.ColorConvertFloat4ToU32(new Vector4(.92f, .79f, .48f, 1f)), rounding,
            ImDrawFlags.None, 1.5f * scale);
        drawList.AddText(position + (size - textSize) / 2, ImGui.ColorConvertFloat4ToU32(Vector4.One), label);
    }

    private static void DrawIntensityFooter(CardCategory categories, Vector2 cardSize)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var windowPosition = ImGui.GetWindowPos();
        var plaqueTop = windowPosition + new Vector2(cardSize.X * .29f, cardSize.Y * .8375f);
        var plaqueSize = new Vector2(cardSize.X * .42f, cardSize.Y * .024f);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(plaqueTop, plaqueTop + plaqueSize,
            ImGui.ColorConvertFloat4ToU32(new Vector4(.34f, .09f, .12f, .96f)), 3 * scale);
        drawList.AddRect(plaqueTop, plaqueTop + plaqueSize,
            ImGui.ColorConvertFloat4ToU32(new Vector4(.76f, .59f, .28f, 1f)), 3 * scale,
            ImDrawFlags.None, 1.5f * scale);
        var label = "I N T E N S I T Y";
        var labelSize = ImGui.CalcTextSize(label);
        drawList.AddText(plaqueTop + (plaqueSize - labelSize) / 2,
            ImGui.ColorConvertFloat4ToU32(new Vector4(.96f, .87f, .67f, 1f)), label);

        var slotY = windowPosition.Y + cardSize.Y * .891f;
        var slotWidth = cardSize.X * .178f;
        var slotHeight = 24 * scale;
        var centers = new[] { .21f, .405f, .595f, .785f };
        for (var index = 0; index < BasicCategories.Length; index++)
        {
            var category = BasicCategories[index];
            var selected = categories.HasFlag(category);
            var position = new Vector2(windowPosition.X + cardSize.X * centers[index] - slotWidth / 2, slotY);
            DrawFixedCategorySlot(category, selected, position, new Vector2(slotWidth, slotHeight));
        }
    }

    private static void DrawFixedCategorySlot(CardCategory category, bool selected, Vector2 position, Vector2 size)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetWindowDrawList();
        var baseColor = CategoryColor(category);
        var fill = selected
            ? new Vector4(baseColor.X, baseColor.Y, baseColor.Z, 1f)
            : new Vector4(baseColor.X * .48f, baseColor.Y * .48f, baseColor.Z * .48f, .78f);
        var border = selected
            ? new Vector4(.96f, .84f, .56f, 1f)
            : new Vector4(.60f, .47f, .27f, .72f);
        var rounding = 4 * scale;
        drawList.AddRectFilled(position, position + size, ImGui.ColorConvertFloat4ToU32(fill), rounding);
        drawList.AddRect(position, position + size, ImGui.ColorConvertFloat4ToU32(border), rounding,
            ImDrawFlags.None, (selected ? 1.8f : 1.1f) * scale);
        var label = SingleCategoryLabel(category);
        var textSize = ImGui.CalcTextSize(label);
        var textColor = selected ? Vector4.One : new Vector4(.78f, .72f, .63f, .82f);
        drawList.AddText(position + (size - textSize) / 2, ImGui.ColorConvertFloat4ToU32(textColor), label);
    }

    private IFontHandle CreateCardFont(float size, bool bold, bool italic)
    {
        var style = new GameFontStyle(GameFontFamily.Axis, size) { Bold = bold, Italic = italic };
        return Plugin.PluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(style);
    }

    private void AppendFormatting(string tag, string placeholder)
    {
        var insertion = $"[{tag}]{placeholder}[/{tag}]";
        if (cardText.Length > 0 && !char.IsWhiteSpace(cardText[^1])) insertion = " " + insertion;
        if (cardText.Length + insertion.Length > MaxCardText)
        {
            SetStatus("The card text is too long to add formatting.", true);
            return;
        }
        cardText += insertion;
    }

    private void DrawFormattedCardText(string text, Vector2 cardSize, Vector4 color)
    {
        var origin = ImGui.GetWindowPos() + new Vector2(cardSize.X * 0.075f, cardSize.Y * 0.56f);
        var maxX = ImGui.GetWindowPos().X + cardSize.X * 0.925f;
        var contentWidth = maxX - origin.X;
        var baseLineHeight = 24f * ImGuiHelpers.GlobalScale;
        var availableHeight = cardSize.Y * 0.16f;
        var textScale = 1f;
        var lines = LayoutFormattedLines(text, contentWidth);
        while (textScale > .76f && lines.Count > Math.Max(1, (int)MathF.Floor(availableHeight / (baseLineHeight * textScale))))
        {
            textScale = MathF.Max(.76f, textScale - .06f);
            lines = LayoutFormattedLines(text, contentWidth / textScale);
        }
        var y = origin.Y;
        var lineHeight = baseLineHeight * textScale;
        var maximumLines = Math.Max(1, (int)MathF.Floor(availableHeight / lineHeight));

        foreach (var line in lines.Take(maximumLines))
        {
            var renderedWidth = line.Width * textScale;
            var x = line.Centered ? origin.X + MathF.Max(0, (contentWidth - renderedWidth) / 2f) : origin.X;
            foreach (var token in line.Tokens)
            {
                using var pushedFont = FontFor(token.Bold, token.Italic).Push();
                ImGui.SetWindowFontScale(textScale);
                ImGui.SetCursorScreenPos(new Vector2(x, y));
                ImGui.TextColored(color, token.Text);
                if (token.Underline && token.Text.Trim().Length > 0)
                {
                    var underlineY = y + token.Size.Y * textScale + ImGuiHelpers.GlobalScale;
                    ImGui.GetWindowDrawList().AddLine(new Vector2(x, underlineY),
                        new Vector2(x + token.Size.X * textScale, underlineY), ImGui.ColorConvertFloat4ToU32(color),
                        ImGuiHelpers.GlobalScale);
                }
                x += token.Size.X * textScale;
                ImGui.SetWindowFontScale(1f);
            }
            y += lineHeight;
        }
    }

    private void DrawFlavorText(string text, Vector2 cardSize, Vector4 color)
    {
        var origin = ImGui.GetWindowPos() + new Vector2(cardSize.X * 0.075f, cardSize.Y * 0.77f);
        var contentWidth = cardSize.X * 0.85f;
        var baseLineHeight = 18f * ImGuiHelpers.GlobalScale;
        var availableHeight = cardSize.Y * .057f;
        var textScale = 1f;
        var lines = LayoutFlavorLines(text, contentWidth);
        while (textScale > .86f && lines.Count > Math.Max(1, (int)MathF.Floor(availableHeight / (baseLineHeight * textScale))))
        {
            textScale = MathF.Max(.86f, textScale - .04f);
            lines = LayoutFlavorLines(text, contentWidth / textScale);
        }
        var y = origin.Y;

        var lineHeight = baseLineHeight * textScale;
        var maximumLines = Math.Max(1, (int)MathF.Floor(availableHeight / lineHeight));
        foreach (var line in lines.Take(maximumLines))
        {
            var x = origin.X + MathF.Max(0, (contentWidth - line.Width * textScale) / 2f);
            foreach (var token in line.Tokens)
            {
                using var pushedFont = flavorFont.Push();
                ImGui.SetWindowFontScale(textScale);
                ImGui.SetCursorScreenPos(new Vector2(x, y));
                ImGui.TextColored(new Vector4(color.X, color.Y, color.Z, .82f), token.Text);
                x += token.Size.X * textScale;
                ImGui.SetWindowFontScale(1f);
            }
            y += lineHeight;
        }
    }

    private List<FormattedLine> LayoutFlavorLines(string text, float maximumWidth)
    {
        var lines = new List<FormattedLine>();
        var current = new FormattedLine(true);
        lines.Add(current);

        foreach (var chunk in System.Text.RegularExpressions.Regex.Split(text.Replace("\r", string.Empty), "(\\s+)"))
        {
            if (chunk.Length == 0) continue;
            var parts = chunk.Split('\n');
            for (var index = 0; index < parts.Length; index++)
            {
                if (index > 0)
                {
                    current = new FormattedLine(true);
                    lines.Add(current);
                }
                if (chunk.Contains('\n') && string.IsNullOrWhiteSpace(parts[index])) continue;
                var value = string.IsNullOrWhiteSpace(parts[index]) ? " " : parts[index];
                if (value == " " && current.Tokens.Count == 0) continue;
                using var pushedFont = flavorFont.Push();
                var size = ImGui.CalcTextSize(value);
                if (current.Tokens.Count > 0 && current.Width + size.X > maximumWidth)
                {
                    current = new FormattedLine(true);
                    lines.Add(current);
                    value = value.TrimStart();
                    if (value.Length == 0) continue;
                    size = ImGui.CalcTextSize(value);
                }
                current.Tokens.Add(new FormattedToken(value, false, true, false, size));
                current.Width += size.X;
            }
        }

        return lines;
    }

    private List<FormattedLine> LayoutFormattedLines(string text, float maximumWidth)
    {
        var lines = new List<FormattedLine>();
        var current = new FormattedLine(false);
        lines.Add(current);

        foreach (var run in ParseFormatting(text))
        {
            if (current.Tokens.Count > 0 && current.Centered != run.Centered)
            {
                current = new FormattedLine(run.Centered);
                lines.Add(current);
            }
            else current.Centered = run.Centered;

            var chunks = System.Text.RegularExpressions.Regex.Split(run.Text.Replace("\r", string.Empty), "(\\s+)");
            foreach (var chunk in chunks)
            {
                if (chunk.Length == 0) continue;
                var newlineParts = chunk.Split('\n');
                for (var index = 0; index < newlineParts.Length; index++)
                {
                    if (index > 0)
                    {
                        current = new FormattedLine(run.Centered);
                        lines.Add(current);
                    }

                    if (chunk.Contains('\n') && string.IsNullOrWhiteSpace(newlineParts[index])) continue;
                    var value = string.IsNullOrWhiteSpace(newlineParts[index]) ? " " : newlineParts[index];
                    if (value == " " && current.Tokens.Count == 0) continue;
                    using var pushedFont = FontFor(run.Bold, run.Italic).Push();
                    var size = ImGui.CalcTextSize(value);
                    if (current.Tokens.Count > 0 && current.Width + size.X > maximumWidth)
                    {
                        current = new FormattedLine(run.Centered);
                        lines.Add(current);
                        value = value.TrimStart();
                        if (value.Length == 0) continue;
                        size = ImGui.CalcTextSize(value);
                    }
                    current.Tokens.Add(new FormattedToken(value, run.Bold, run.Italic, run.Underline, size));
                    current.Width += size.X;
                }
            }
        }

        return lines;
    }

    private IFontHandle FontFor(bool bold, bool italic) => (bold, italic) switch
    {
        (true, true) => cardBoldItalicFont,
        (true, false) => cardBoldFont,
        (false, true) => cardItalicFont,
        _ => cardFont,
    };

    private static IEnumerable<FormattedRun> ParseFormatting(string text)
    {
        var bold = false;
        var italic = false;
        var underline = false;
        var centered = false;
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '[') continue;
            var close = text.IndexOf(']', index + 1);
            if (close < 0) break;
            var tag = text[(index + 1)..close].ToLowerInvariant();
            if (tag is not ("b" or "/b" or "i" or "/i" or "u" or "/u" or "c" or "/c")) continue;
            if (index > start) yield return new FormattedRun(text[start..index], bold, italic, underline, centered);
            switch (tag)
            {
                case "b": bold = true; break;
                case "/b": bold = false; break;
                case "i": italic = true; break;
                case "/i": italic = false; break;
                case "u": underline = true; break;
                case "/u": underline = false; break;
                case "c": centered = true; break;
                case "/c": centered = false; break;
            }
            index = close;
            start = close + 1;
        }
        if (start < text.Length) yield return new FormattedRun(text[start..], bold, italic, underline, centered);
    }

    private static string StripFormatting(string text)
    {
        var result = text;
        foreach (var tag in new[] { "b", "i", "u", "c" })
        {
            result = result.Replace($"[{tag}]", string.Empty, StringComparison.OrdinalIgnoreCase);
            result = result.Replace($"[/{tag}]", string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        return result;
    }

    private readonly record struct FormattedRun(string Text, bool Bold, bool Italic, bool Underline, bool Centered);
    private readonly record struct FormattedToken(string Text, bool Bold, bool Italic, bool Underline, Vector2 Size);

    private sealed class FormattedLine(bool centered)
    {
        public bool Centered { get; set; } = centered;
        public float Width { get; set; }
        public List<FormattedToken> Tokens { get; } = [];
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

    private static void DrawCenteredOverlayTextFitted(string text, float y, float extraPoints, float maximumWidth, Vector4 color)
    {
        ImGui.SetCursorPosX(0);
        ImGui.SetCursorPosY(y);
        var originalSize = ImGui.GetFontSize();
        var desiredScale = (originalSize + extraPoints * ImGuiHelpers.GlobalScale) / originalSize;
        var naturalWidth = ImGui.CalcTextSize(text).X * desiredScale;
        var fittedScale = naturalWidth > maximumWidth ? desiredScale * maximumWidth / naturalWidth : desiredScale;
        fittedScale = MathF.Max(.72f, fittedScale);
        ImGui.SetWindowFontScale(fittedScale);
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

    private static string ArtworkLabel(ArtworkChoice artwork) => artwork switch
    {
        ArtworkChoice.SfwAdventurersResolve => "Adventurer's Resolve",
        ArtworkChoice.SfwFiresideFellowship => "Fireside Fellowship",
        ArtworkChoice.SfwScholarsReflection => "Scholar's Reflection",
        ArtworkChoice.SfwStarlitJourney => "Starlit Journey",
        ArtworkChoice.SfwFestivalSpirit => "Festival Spirit",
        ArtworkChoice.SfwGardenSanctuary => "Garden Sanctuary",
        ArtworkChoice.MixedMasquerade => "Masquerade",
        ArtworkChoice.MixedDaringWager => "Daring Wager",
        ArtworkChoice.MixedMidnightConfession => "Midnight Confession",
        ArtworkChoice.MixedTangledChoices => "Tangled Choices",
        ArtworkChoice.MixedMoonlitRendezvous => "Moonlit Rendezvous",
        ArtworkChoice.MixedWildCard => "Wild Card",
        ArtworkChoice.NsfwCrimsonInvitation => "Crimson Invitation",
        ArtworkChoice.NsfwVelvetSecrets => "Velvet Secrets",
        ArtworkChoice.NsfwHeatedChallenge => "Heated Challenge",
        ArtworkChoice.NsfwForbiddenGlance => "Forbidden Glance",
        ArtworkChoice.NsfwRoseAndChain => "Rose and Chain",
        ArtworkChoice.NsfwAfterHours => "After Hours",
        ArtworkChoice.NsfwPlusInfernalPact => "Infernal Pact",
        ArtworkChoice.NsfwPlusScarletTemptation => "Scarlet Temptation",
        ArtworkChoice.NsfwPlusUnboundDesire => "Unbound Desire",
        ArtworkChoice.NsfwPlusMidnightVice => "Midnight Vice",
        ArtworkChoice.NsfwPlusBurningOath => "Burning Oath",
        ArtworkChoice.NsfwPlusNoRestraints => "No Restraints",
        _ => artwork.ToString(),
    };

    private string ArtworkLabel(Card card)
    {
        if (card.CustomArtworkId is not Guid id) return ArtworkLabel(card.Artwork);
        return selectedDeck.CustomArtwork.FirstOrDefault(asset => asset.Id == id)?.Name ?? "Missing custom artwork";
    }

    private string ResolveArtworkPath(Guid? customId, ArtworkChoice builtIn) => customId is Guid id
        ? store.GetArtworkPath(selectedDeck, id)
        : Path.Combine(artworkDirectory, ArtworkFileName(builtIn));

    private static CardCategory ArtworkCategory(ArtworkChoice artwork) => artwork switch
    {
        <= ArtworkChoice.SfwGardenSanctuary => CardCategory.Sfw,
        <= ArtworkChoice.MixedWildCard => CardCategory.Mixed,
        <= ArtworkChoice.NsfwAfterHours => CardCategory.Nsfw,
        _ => CardCategory.NsfwPlus,
    };

    private static string ArtworkFileName(ArtworkChoice artwork) => artwork switch
    {
        ArtworkChoice.SfwAdventurersResolve => "sfw-adventurers-resolve.jpg",
        ArtworkChoice.SfwFiresideFellowship => "sfw-fireside-fellowship.jpg",
        ArtworkChoice.SfwScholarsReflection => "sfw-scholars-reflection.jpg",
        ArtworkChoice.SfwStarlitJourney => "sfw-starlit-journey.jpg",
        ArtworkChoice.SfwFestivalSpirit => "sfw-festival-spirit.jpg",
        ArtworkChoice.SfwGardenSanctuary => "sfw-garden-sanctuary.jpg",
        ArtworkChoice.MixedMasquerade => "mixed-masquerade.jpg",
        ArtworkChoice.MixedDaringWager => "mixed-daring-wager.jpg",
        ArtworkChoice.MixedMidnightConfession => "mixed-midnight-confession.jpg",
        ArtworkChoice.MixedTangledChoices => "mixed-tangled-choices.jpg",
        ArtworkChoice.MixedMoonlitRendezvous => "mixed-moonlit-rendezvous.jpg",
        ArtworkChoice.MixedWildCard => "mixed-wild-card.jpg",
        ArtworkChoice.NsfwCrimsonInvitation => "nsfw-crimson-invitation.jpg",
        ArtworkChoice.NsfwVelvetSecrets => "nsfw-velvet-secrets.jpg",
        ArtworkChoice.NsfwHeatedChallenge => "nsfw-heated-challenge.jpg",
        ArtworkChoice.NsfwForbiddenGlance => "nsfw-forbidden-glance.jpg",
        ArtworkChoice.NsfwRoseAndChain => "nsfw-rose-and-chain.jpg",
        ArtworkChoice.NsfwAfterHours => "nsfw-after-hours.jpg",
        ArtworkChoice.NsfwPlusInfernalPact => "nsfwplus-infernal-pact.jpg",
        ArtworkChoice.NsfwPlusScarletTemptation => "nsfwplus-scarlet-temptation.jpg",
        ArtworkChoice.NsfwPlusUnboundDesire => "nsfwplus-unbound-desire.jpg",
        ArtworkChoice.NsfwPlusMidnightVice => "nsfwplus-midnight-vice.jpg",
        ArtworkChoice.NsfwPlusBurningOath => "nsfwplus-burning-oath.jpg",
        ArtworkChoice.NsfwPlusNoRestraints => "nsfwplus-no-restraints.jpg",
        _ => "sfw-adventurers-resolve.jpg",
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
