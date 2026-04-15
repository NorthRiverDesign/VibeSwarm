using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Models;

/// <summary>
/// Identifies which installer strategy will handle a <see cref="SkillInstallRequest"/>.
/// </summary>
public enum SkillInstallSource
{
	/// <summary>
	/// Claude-exported <c>.skill</c> ZIP archive uploaded by the user.
	/// </summary>
	Zip = 0,

	/// <summary>
	/// Skill subfolder from the <c>github.com/anthropics/skills</c> catalog.
	/// Populated in Phase 2.
	/// </summary>
	Marketplace = 1,

	/// <summary>
	/// Folder on the server's local filesystem. Populated in Phase 3.
	/// </summary>
	LocalPath = 2,
}

/// <summary>
/// Input envelope describing where to install a skill from. Exactly one of the source-specific
/// fields should be populated based on <see cref="Source"/>.
/// </summary>
public sealed class SkillInstallRequest
{
	public SkillInstallSource Source { get; init; }

	/// <summary>Original filename for ZIP uploads; surfaced in the preview as provenance.</summary>
	public string? FileName { get; init; }

	/// <summary>Raw bytes of a <c>.skill</c> archive when <see cref="Source"/> is <see cref="SkillInstallSource.Zip"/>.</summary>
	public byte[]? ZipContent { get; init; }

	/// <summary>Absolute path to a folder containing SKILL.md when <see cref="Source"/> is <see cref="SkillInstallSource.LocalPath"/>.</summary>
	public string? LocalPath { get; init; }

	/// <summary>Subfolder name under <c>anthropics/skills</c> when <see cref="Source"/> is <see cref="SkillInstallSource.Marketplace"/>.</summary>
	public string? MarketplaceSlug { get; init; }
}

/// <summary>
/// A single file that would be installed. Used by the preview UX to show the user what
/// the skill carries, particularly any executable scripts.
/// </summary>
public sealed class SkillFileEntry
{
	public required string RelativePath { get; init; }
	public long Size { get; init; }
	public bool IsExecutable { get; init; }
}

/// <summary>
/// Everything the UI needs to render a pre-install confirmation screen: skill identity,
/// SKILL.md body, the source's <c>allowed-tools</c> value, the file inventory, and any
/// warnings surfaced during parsing (missing metadata, large files, unsafe tool patterns).
/// </summary>
public sealed record SkillInstallPreview
{
	public required string Name { get; init; }
	public string? Description { get; init; }
	public string Content { get; init; } = string.Empty;
	public string? AllowedTools { get; init; }
	public bool IsEnabled { get; init; } = true;
	public bool NameExists { get; init; }

	public SkillSourceType SourceType { get; init; }
	public string? SourceUri { get; init; }
	public string? SourceRef { get; init; }

	public bool HasScripts { get; init; }
	public IReadOnlyList<SkillFileEntry> Files { get; init; } = [];
	public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Outcome of a single-shot install that combines preview + commit in one call.
/// Mirrors the <see cref="SkillImportResult"/> shape so existing ZIP upload callers can
/// migrate without UX changes.
/// </summary>
public sealed class SkillInstallResult
{
	public bool Installed { get; init; }
	public bool Skipped { get; init; }
	public string Message { get; init; } = string.Empty;
	public Skill? Skill { get; init; }
	public SkillInstallPreview? Preview { get; init; }
	public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// A single entry in the marketplace catalog — one skill folder under
/// <c>github.com/anthropics/skills</c>. The slug is the top-level folder name and is used
/// as the install request identifier; the other fields come from SKILL.md frontmatter.
/// </summary>
public sealed record MarketplaceSkillSummary
{
	public required string Slug { get; init; }
	public required string Name { get; init; }
	public string? Description { get; init; }
	public string? AllowedTools { get; init; }

	/// <summary>
	/// Commit SHA or branch/tag reference captured when the catalog was fetched.
	/// Used as <see cref="Skill.SourceRef"/> after install so we can detect upstream drift.
	/// </summary>
	public required string Ref { get; init; }
}
