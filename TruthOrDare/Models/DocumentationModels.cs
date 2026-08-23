using System.Collections.Generic;

namespace TruthOrDare.Models;

public enum DocumentationBlockKind { Paragraph, Tip, Warning, Important, Code }
public sealed record DocumentationBlock(DocumentationBlockKind Kind, string Text);
public sealed record HelpSection(string Id, string Icon, string Title, IReadOnlyList<DocumentationBlock> Blocks, bool DefaultOpen = false);
public sealed record HelpDocument(string Title, string Subtitle, IReadOnlyList<HelpSection> Sections);

public enum PatchBadge { New, Improved, Fixed, Important }
public sealed record PatchNoteItem(string Icon, string Title, string Description, PatchBadge? Badge = null);
public sealed record PatchNoteSection(string Title, IReadOnlyList<PatchNoteItem> Items);
public sealed record PatchNoteRelease(string Version, string ReleaseDate, string Title,
    IReadOnlyList<PatchNoteSection> Sections, bool IsPrerelease = false);
