# Levemetes for Dalamud

![Levemetes icon](images/icon.png)

A local-first challenge-card game for Final Fantasy XIV. It uses native Dalamud/ImGui UI, stores decks in the plugin configuration directory, and has no telemetry or web app. Local play remains the default; an optional experimental Direct Private Game mode connects trusted players without a Levemetes-hosted server.

## Features

- Assign one or more of SFW, Mixed, NSFW, and NSFW+ to each card, then choose a play category and draw without replacement from its filtered, shuffled pile.
- Category-colored card labels and text: green, yellow, light red, and dark red respectively.
- Multi-category cards use individually colored headings and an evenly segmented multicolor border.
- Revealed cards use a padded, double-line celestial-gold inner frame with chamfered corners and diamond accents.
- Optional BLIND VOLUNTEER, CHOICE, and RANDOM card keywords.
- Required levemete-style titles and six activity types: Action (Self), Action (Other-Volunteer), Action (Choice), Action (Random), Revelation (Thought), and Revelation (Experience).
- Activity type and artwork are selected independently for every card.
- Includes 24 original selectable artwork scenes: six each under SFW, MIXED, NSFW, and NSFW+.
- Import personal PNG, JPEG, BMP, or GIF artwork in the card editor. Images are automatically center-cropped and resized to an efficient 768×512 JPEG.
- Six original embedded portrait card-face templates, one for every activity type. Runtime title, classification, keyword, and instruction text is overlaid into each template's reserved parchment regions.
- A concealed deck using the supplied celestial-tree card-back image until a card is drawn.
- A clipboard button for copying a drawn card's text before optionally pasting it into chat.
- Add, edit, and delete cards in game.
- Format card text with bold, italic, underline, and individually centered lines, including combined styles.
- Add optional flavor text that appears separately at the bottom of a drawn card.
- Create, rename, switch between, and delete multiple decks.
- Add an optional deck author, shown beside the selected deck name and carried with shared deck bundles.
- Automatic local JSON persistence with a starter deck on first launch.
- Import portable `.levemetesdeck` bundles (or legacy JSON decks) with a file browser, either as a new deck or merged into an existing deck with duplicate cards and images skipped.
- Export a deck and its custom artwork together as one shareable `.levemetesdeck` file.
- Open the most recent export folder directly from the folder button beside the export control.
- Optionally host or join an encrypted Direct Private Game for up to eight trusted players using a private invitation and automatic `Character Name @ Home World` labels.
- Synchronize the host's locked deck and custom artwork once, then share ordered card-draw and reset events without repeatedly transferring card content.
- Input limits, format-version checks, duplicate-ID repair, safe replacement saves, delete confirmations, and visible error messages.

Draw state is session-only. Deck contents persist, but the draw pile resets when the plugin reloads or a deck changes.

## Experimental Direct Private Game

Direct Private Game is an optional peer-to-peer host mode under its own tab. The existing local game does not depend on it and remains available when the option is disabled or after leaving a room.

To host:

1. Select the deck and intensity category to use.
2. Enable **Experimental Direct Private Game**.
3. Confirm the automatically detected character name and home world, then enter the public IP address or DNS name guests will use and a listening port between 1024 and 65535.
4. Choose **Create Direct Room** and allow the connection through Windows Firewall if prompted.
5. Configure TCP port forwarding on the host's router when required.
6. Copy the generated invitation and share it privately with trusted players.

To join, enable the experimental option while logged in to a character, paste the full invitation, and choose **Join with Invitation**. Joining imports the host's synchronized deck and custom artwork into the guest's local deck collection. Everyone must use a compatible Levemetes version. Room lists show only each participant's locally detected character name and home world; Levemetes never displays peer IP addresses in the player list.

The room supports at most eight players. The host owns the shuffled draw pile, resolves every draw request, and is the only player who can shuffle/reset. The synchronized deck and intensity category are locked until the room closes. There is no public room listing, automatic party discovery, host migration, NAT traversal, or relay fallback in this experimental version.

Direct messages use a random invitation secret, per-connection derived keys, AES-GCM authenticated encryption, ordered frame counters, strict frame-size limits, and validated `.levemetesdeck` content. The invitation itself contains a secret key and must be treated like a password.

**IP privacy warning:** direct networking cannot hide the addresses used to route the connection. Guests can see the host's IP address, and the host can see connecting guest addresses. Hashing protects deck integrity and duplicate detection; it does not anonymize IP addresses. Use Direct Private Game only with people you trust.

## Requirements

- Windows with Final Fantasy XIV, XIVLauncher, and Dalamud.
- Visual Studio 2026 or another IDE that supports the .NET 10 SDK.
- .NET 10 SDK.
- Internet access for the first package restore.

This project targets **Dalamud API 15** using `Dalamud.NET.Sdk/15.0.0` and .NET 10, matching the current release conventions as of August 2026.

## Build

1. Open `TruthOrDare.slnx` in Visual Studio, or open a terminal in this directory.
2. Restore and build:

   ```powershell
   dotnet restore
   dotnet build -c Debug
   ```

3. The development plugin will be produced under `TruthOrDare/bin/x64/Debug/` (the SDK may add a `publish` subdirectory depending on its version). Locate `Levemetes.dll` there.

Before publishing, replace the placeholder project URL in `TruthOrDare/TruthOrDare.csproj`.

## Install for local development

1. Launch the game through XIVLauncher.
2. Open Dalamud Settings with `/xlsettings`.
3. Enable plugin development/testing options if needed.
4. Under **Experimental → Dev Plugin Locations**, add the full path to the built `Levemetes.dll` (or its containing output directory).
5. Open `/xlplugins`, find the plugin under developer plugins, and enable it.
6. Use `/levemetes` to open the window. The plugin installer’s main/configuration buttons also open it.

After rebuilding, use Dalamud’s developer plugin reload control or restart the game.

## Deck files and sharing

Live decks are stored under Dalamud’s per-plugin configuration folder in `decks/`. The exact path is displayed on the **Decks & Sharing** tab.

For sharing, enter a bundle path and choose **Export Selected**, then send the resulting `.levemetesdeck` file outside the game. The bundle contains `deck.json` plus only that deck's custom images; the 24 built-in images are already supplied by the plugin. Another player can use **Import as New Deck...** and select it in the file browser. Imported decks receive new internal IDs, so they do not overwrite an existing deck.

Use **Add Custom Image...** beside the Artwork selector to add personal artwork. Levemetes validates the file, center-crops it to the card's artwork shape, resizes it to 768×512, and stores an optimized local JPEG. The preview shows the same visible crop used by a drawn card. An unused custom image can be removed from the editor; images still assigned to cards are protected from accidental removal.

To combine decks, select the destination deck and choose **Merge into Selected...**. Cards that match an existing card's title, activity type, artwork content, categories, optional keyword, and text are skipped; capitalization and extra whitespace do not create false differences. Identical custom images are reused, and new cards and artwork receive fresh internal IDs.

The card editor includes **Bold**, **Italic**, **Underline**, and **Center Line** buttons. Each button inserts a short editable example using `[b]`, `[i]`, `[u]`, or `[c]` tags. Replace the example words with the text you want formatted. Tags may be nested to combine styles. A `[c]...[/c]` section is centered independently without centering the rest of the card. The tags are rendered as formatting on the card and removed when **Copy Text of Card** is used.

Each card may also have optional flavor text. Flavor text is displayed as a separate centered, italic section in the final two or three lines at the bottom of the card. **Copy Text of Card** copies only the playable card text and deliberately excludes the flavor text.

Example deck format:

```json
{
  "FormatVersion": 8,
  "Id": "92310d1a-3255-4dd0-ab1d-51007a1a5812",
  "Name": "Example Deck",
  "Author": "Example Creator",
  "Cards": [
    {
      "Id": "cdf69327-51b8-4731-9088-acba915551db",
      "Title": "A Matter of Perspective",
      "Activity": "RevelationThought",
      "Category": "Sfw, Mixed",
      "Keyword": "Choice",
      "Text": "Example card text"
    },
    {
      "Id": "bbb0f52c-9f10-49c8-b1d8-b59d11c9e2fa",
      "Title": "Lend a Hand",
      "Activity": "ActionOtherVolunteer",
      "Category": "Mixed",
      "Keyword": null,
      "Text": "Another example card"
    }
  ]
}
```

Deck names and optional author names are limited to 80 characters, card text to 1,000 characters, flavor text to 240 characters, and decks to 5,000 cards and 200 custom images. Source images are limited to 25 MB and bundles to 100 MB. Bundle contents are validated and never extracted by their supplied paths. Import only files you trust and review their text before playing.

## Privacy and scope

Local mode does not contact a network service. Direct Private Game opens a host-controlled TCP listener or connects directly to the address in an invitation, transfers the locked deck to joining guests, and synchronizes character labels and game events. The interface does not show peer connection addresses, although direct participants can still discover them using operating-system networking tools. Levemetes does not provide a relay, account service, public lobby, telemetry system, or automatic chat posting.

## License

MIT for the plugin source. The six activity templates are generated placeholders and can be replaced later without changing deck data. The bundled card back and user-supplied artwork/screenshots remain subject to their respective creators' rights and applicable game-material usage rules; confirm redistribution permission before publishing them.

## Submission note

Dalamud’s official repository has contribution and AI-usage policies beyond merely compiling successfully. Review the current policies and perform a human code review and play test before any submission.
