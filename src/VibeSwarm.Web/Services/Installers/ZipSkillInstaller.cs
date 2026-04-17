using System.ComponentModel.DataAnnotations;
using System.IO.Compression;
using System.Text;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;

namespace VibeSwarm.Shared.Services;

/// <summary>
/// Installer strategy for Claude-exported <c>.skill</c> ZIP archives. Parses SKILL.md
/// from the archive root, merges any <c>references/</c> markdown into the skill's body
/// for back-compat with how skills were stored historically, and stages the folder so the
/// orchestrator can copy it into central storage.
/// </summary>
public sealed class ZipSkillInstaller : ISkillInstaller
{
	public SkillInstallSource Source => SkillInstallSource.Zip;

	public Task<(SkillInstallPreview Preview, string StagedDirectory)> StageAsync(
		SkillInstallRequest request,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		if (request.Source != SkillInstallSource.Zip)
		{
			throw new ArgumentException($"ZipSkillInstaller cannot handle source '{request.Source}'.", nameof(request));
		}

		if (string.IsNullOrWhiteSpace(request.FileName))
		{
			throw new ValidationException("File name is required.");
		}

		if (request.ZipContent is null || request.ZipContent.Length == 0)
		{
			throw new ValidationException("Skill file content is empty.");
		}

		if (!request.FileName.EndsWith(".skill", StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException("Only Claude-exported .skill files are supported.");
		}

		using var archiveStream = new MemoryStream(request.ZipContent, writable: false);
		using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);

		var skillEntry = archive.Entries
			.FirstOrDefault(entry => entry.FullName.EndsWith("/SKILL.md", StringComparison.OrdinalIgnoreCase));
		if (skillEntry is null)
		{
			throw new InvalidDataException("The .skill archive is missing SKILL.md.");
		}

		var skillMarkdown = ReadEntryAsString(skillEntry);
		if (string.IsNullOrWhiteSpace(skillMarkdown))
		{
			throw new InvalidDataException("The .skill archive contains an empty SKILL.md file.");
		}

		var warnings = new List<string>();
		var files = new List<SkillFileEntry>
		{
			new() { RelativePath = "SKILL.md", Size = skillEntry.Length },
		};

		var (metadata, body) = SkillManifestParser.ParseFrontMatter(skillMarkdown);
		var fallbackName = Path.GetFileNameWithoutExtension(request.FileName);
		var name = SkillManifestParser.NormalizeName(SkillManifestParser.GetValue(metadata, "name"), fallbackName);
		var description = SkillManifestParser.NormalizeDescription(SkillManifestParser.GetValue(metadata, "description"), warnings);
		var allowedTools = NormalizeAllowedTools(SkillManifestParser.GetValue(metadata, "allowed-tools"));

		var contentBuilder = new StringBuilder(body.Trim());
		var referenceEntries = archive.Entries
			.Where(entry =>
				!string.IsNullOrEmpty(entry.Name) &&
				entry.FullName.Contains("/references/", StringComparison.OrdinalIgnoreCase))
			.OrderBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase)
			.ToList();

		foreach (var referenceEntry in referenceEntries)
		{
			var referenceContent = ReadEntryAsString(referenceEntry).Trim();
			if (string.IsNullOrWhiteSpace(referenceContent))
			{
				continue;
			}

			var relativePath = TrimArchiveRoot(referenceEntry.FullName);
			files.Add(new SkillFileEntry { RelativePath = relativePath, Size = referenceEntry.Length });
			contentBuilder
				.AppendLine()
				.AppendLine()
				.AppendLine("---")
				.AppendLine()
				.AppendLine($"## Reference: {Path.GetFileNameWithoutExtension(referenceEntry.Name)}")
				.AppendLine()
				.AppendLine(referenceContent);
		}

		var content = contentBuilder.ToString().Trim();
		if (string.IsNullOrWhiteSpace(content))
		{
			throw new ValidationException("Imported skill content is empty.");
		}

		// Stage the merged SKILL.md on disk under a temp folder. .skill archives historically
		// don't carry scripts/, so HasScripts is always false and we don't materialize a
		// references/ subtree — the reference content is already merged into the body above.
		var stagedDirectory = Path.Combine(Path.GetTempPath(), "vibeswarm", "skill-install", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(stagedDirectory);
		File.WriteAllText(Path.Combine(stagedDirectory, "SKILL.md"), content);

		var preview = new SkillInstallPreview
		{
			Name = name,
			Description = description,
			Content = content,
			AllowedTools = allowedTools,
			IsEnabled = true,
			SourceType = SkillSourceType.ZipImport,
			SourceUri = request.FileName,
			SourceRef = null,
			HasScripts = false,
			Files = files,
			Warnings = warnings,
		};

		return Task.FromResult((preview, stagedDirectory));
	}

	private static string TrimArchiveRoot(string fullName)
	{
		// Claude exports archives rooted at a single folder matching the skill name. Strip
		// that root so the preview file list reads like the on-disk layout (SKILL.md, references/*).
		var firstSlash = fullName.IndexOf('/');
		return firstSlash >= 0 && firstSlash < fullName.Length - 1
			? fullName[(firstSlash + 1)..]
			: fullName;
	}

	private static string ReadEntryAsString(ZipArchiveEntry entry)
	{
		using var stream = entry.Open();
		using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
		return reader.ReadToEnd();
	}

	private static string? NormalizeAllowedTools(string? raw)
	{
		if (string.IsNullOrWhiteSpace(raw))
		{
			return null;
		}

		var trimmed = raw.Trim();
		// Claude frontmatter can use either a space-separated list on one line or a YAML
		// folded scalar (already joined by ParseFrontMatter). Either way we store the raw
		// string verbatim for transparency in the UI; validation of the allow-list semantics
		// lives in the preview warnings surface, not here.
		return trimmed.Length == 0 ? null : trimmed;
	}
}
