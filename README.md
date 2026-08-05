# Levemetes for Dalamud

A small, fully local challenge-card game for Final Fantasy XIV. It uses native Dalamud/ImGui UI, stores decks as readable JSON in the plugin configuration directory, and has no server, telemetry, or web app.

The custom repository for this plugin can be found at https://raw.githubusercontent.com/kaerlath/Levemetes/main/repo.json

## Features

- Assign one or more of SFW, Mixed, NSFW, and NSFW+ to each card, then choose a play category and draw without replacement from its filtered, shuffled pile.
- Category-colored card labels and text: green, yellow, light red, and dark red respectively.
- Multi-category cards use individually colored headings and an evenly segmented multicolor border.
- Revealed cards use a padded, double-line celestial-gold inner frame with chamfered corners and diamond accents.
- Optional BLIND VOLUNTEER, CHOICE, and RANDOM card keywords.
- Required levemete-style titles and six activity types: Action (Self), Action (Other-Volunteer), Action (Choice), Action (Random), Revelation (Thought), and Revelation (Experience).
- Six original embedded portrait card-face templates, one for every activity type. Runtime title, classification, keyword, and instruction text is overlaid into each template's reserved parchment regions.
- A concealed deck using the supplied celestial-tree card-back image until a card is drawn.
- A clipboard button for copying a drawn card's text before optionally pasting it into chat.
- Add, edit, and delete cards in game.
- Create, rename, switch between, and delete multiple decks.
- Automatic local JSON persistence with a starter deck on first launch.
- Import and export portable JSON deck files using a file path.
- Input limits, format-version checks, duplicate-ID repair, safe replacement saves, delete confirmations, and visible error messages.

Draw state is session-only. Deck contents persist, but the draw pile resets when the plugin reloads or a deck changes.

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

For sharing, enter a JSON path and choose **Export Selected**. Send that exported file outside the game. Another player can enter its path and choose **Import as New Deck**. Imported decks receive a new internal ID, so they do not overwrite an existing deck.

Example deck format:

```json
{
  "FormatVersion": 4,
  "Id": "92310d1a-3255-4dd0-ab1d-51007a1a5812",
  "Name": "Example Deck",
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

Deck names are limited to 80 characters, card text to 1,000 characters, and imported files to 5 MB / 5,000 cards. Import only files you trust and review their text before playing.

## Privacy and scope

Everything happens on the local client. The plugin does not synchronize players, post to chat, read game state, or contact a network service. Players coordinate turns and share exported files themselves.

## License

MIT for the plugin source. The six activity templates are original generated placeholders and can be replaced later without changing deck data. The bundled `card-back.jpg` is the user-specified image from `eternalstarco.carrd.co`; confirm that you have permission to redistribute it before publishing the plugin. No Final Fantasy XIV visual assets are included.

## Submission note

Dalamud’s official repository has contribution and AI-usage policies beyond merely compiling successfully. Review the current policies and perform a human code review and play test before any submission.
