using TruthOrDare.Models;

namespace TruthOrDare.Content;

public static class HelpContent
{
    private static DocumentationBlock P(string text) => new(DocumentationBlockKind.Paragraph, text);
    private static DocumentationBlock Tip(string text) => new(DocumentationBlockKind.Tip, text);
    private static DocumentationBlock Warn(string text) => new(DocumentationBlockKind.Warning, text);
    private static DocumentationBlock Important(string text) => new(DocumentationBlockKind.Important, text);
    private static DocumentationBlock Code(string text) => new(DocumentationBlockKind.Code, text);

    public static readonly HelpDocument UserGuide = new("LEVEMETES USER GUIDE", "From the first draw to a full multiplayer table.",
    [
        new("getting-started", "✦", "Getting Started",
        [P("Select a deck and one Intensity category on the Play tab. Draw reveals the next card, Copy Card Text places only the playable card text on your clipboard, and Shuffle / Reset refills and randomizes the draw pile."), Code("/levemetes"), Tip("Flavor text is decorative and is intentionally excluded when card text is copied.")], true),
        new("cards", "◇", "Creating and Editing Cards",
        [P("Cards can have a title, activity type, one or more Intensity categories, optional keyword, formatted card text, flavor text, and artwork. The live preview updates while you edit."), P("Formatting tags support bold, italic, underline, and centered lines. Cards may belong to several Intensity categories and retain every selected category when exported.")]),
        new("decks", "▣", "Decks, Artwork, and Sharing",
        [P("Create separate decks for different groups or themes. Decks can carry an author, custom card back, built-in or custom artwork, and a last-edited or imported date."), P("Export creates a shareable Levemetes deck bundle. Import can add a new deck, while Merge lets you preview individual non-duplicate cards and choose which ones to add."), Important("Only share artwork that you own or have permission to distribute.")]),
        new("relay", "⌁", "Relay Multiplayer",
        [P("Relay rooms avoid exposing player IP addresses. Public rooms appear in the room list; private rooms use an eight-character code and may also require a password."), P("The host's selected deck and custom artwork are synchronized and locked for the room. The host starts the game, controls resets, may force missing scores to 3, and ends the game."), Tip("Use Copy Code beside a private room code before sending it to invited players.")]),
        new("observer", "◉", "Observer Mode",
        [P("Observers remain in the room and see cards and messages, but do not draw, score, volunteer, enter RANDOM selection, or participate in tie-breaks."), P("A player may toggle their own Observer status. The host may also toggle it for any player. Existing points are preserved, and the player can earn points again after returning to active play.")]),
        new("scoring", "★", "Scoring and Game Results",
        [P("When scoring is enabled, every eligible player except the card's drawer assigns 0–5 points. The next draw waits until eligible votes arrive or the host uses Force Pass."), P("End Game totals the scores. A two-player first-place tie is a shared victory; larger ties may be resolved by the remaining eligible players.")]),
        new("keywords", "!", "Card Keywords",
        [P("RANDOM selects another eligible active player. BLIND VOLUNTEER hides the card while volunteers respond, then reveals it after selection. CHOICE marks a card whose participant is selected by the drawer."), Tip("In relay play, volunteers responding during the first five seconds enter a random pool. Afterward, the first response wins; at 30 seconds an eligible player is chosen automatically.")]),
        new("direct", "↔", "Direct Private Game",
        [P("Direct Private Game connects players peer to peer and therefore exposes network addresses to the participants. Use it only with people you trust."), P("The host forwards TCP port 43871 to the computer's router-assigned local IPv4 address, allows the application through the firewall, and shares the generated invitation."), Warn("Router menus vary. Look for Port Forwarding, NAT, Virtual Server, or Gaming, and never expose unrelated ports.")]),
        new("troubleshooting", "⚙", "Troubleshooting",
        [P("If a relay room cannot be reached, use Check Relay and confirm the official endpoint. If a synchronized deck fails, reconnect after the host recreates the room."), P("If Dalamud does not immediately show an update, refresh repositories or restart the game; repository caches can take a short time to update."), P("For direct connections, verify TCP 43871, the host's current local IPv4 address, Windows Firewall access, and the router forwarding rule.")]),
        new("privacy", "◆", "Privacy and Local Data",
        [P("Decks, imported artwork, configuration, and card backs are stored locally. Relay deck bundles are temporary and are removed by the relay storage lifecycle."), Warn("A Direct Private Game reveals the host address to guests and guest addresses to the host. Relay Multiplayer is the privacy-preserving option.")]),
    ]);
}
