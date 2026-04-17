using System.ComponentModel.DataAnnotations;
using System.Formats.Tar;
using System.IO.Compression;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;

namespace VibeSwarm.Shared.Services;

/// <summary>
/// Installer strategy for skills in the <c>github.com/anthropics/skills</c> marketplace.
/// Resolves the slug against the cached catalog, downloads the repo tarball, and stages only
/// the target skill's subfolder so the orchestrator can copy it into central storage.
/// </summary>
public sealed class MarketplaceSkillInstaller : ISkillInstaller
{
	private readonly IGitHubSkillCatalogClient _catalogClient;

	public MarketplaceSkillInstaller(IGitHubSkillCatalogClient catalogClient)
	{
		_catalogClient = catalogClient;
	}

	public SkillInstallSource Source => SkillInstallSource.Marketplace;

	public async Task<(SkillInstallPreview Preview, string StagedDirectory)> StageAsync(
		SkillInstallRequest request,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		if (request.Source != SkillInstallSource.Marketplace)
		{
			throw new ArgumentException($"MarketplaceSkillInstaller cannot handle source '{request.Source}'.", nameof(request));
		}

		var slug = request.MarketplaceSlug?.Trim();
		if (string.IsNullOrWhiteSpace(slug))
		{
			throw new ValidationException("Marketplace skill slug is required.");
		}

		var catalog = await _catalogClient.ListSkillsAsync(cancellationToken);
		var summary = catalog.FirstOrDefault(item => string.Equals(item.Slug, slug, StringComparison.OrdinalIgnoreCase));
		if (summary is null)
		{
			throw new InvalidOperationException($"Marketplace does not contain a skill with slug '{slug}'.");
		}

		var stagedDirectory = Path.Combine(Path.GetTempPath(), "vibeswarm", "skill-install", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(stagedDirectory);

		try
		{
			await DownloadAndExtractFolderAsync(summary.Slug, summary.Ref, stagedDirectory, cancellationToken);
		}
		catch
		{
			TryDeleteDirectory(stagedDirectory);
			throw;
		}

		var manifestPath = Path.Combine(stagedDirectory, "SKILL.md");
		if (!File.Exists(manifestPath))
		{
			TryDeleteDirectory(stagedDirectory);
			throw new InvalidDataException($"Marketplace skill '{slug}' is missing SKILL.md after download.");
		}

		var manifest = await File.ReadAllTextAsync(manifestPath, cancellationToken);
		var (metadata, body) = SkillManifestParser.ParseFrontMatter(manifest);
		var warnings = new List<string>();
		var name = SkillManifestParser.NormalizeName(SkillManifestParser.GetValue(metadata, "name"), summary.Slug);
		var description = SkillManifestParser.NormalizeDescription(SkillManifestParser.GetValue(metadata, "description"), warnings);
		var allowedTools = NormalizeAllowedTools(SkillManifestParser.GetValue(metadata, "allowed-tools"));
		if (string.IsNullOrWhiteSpace(allowedTools))
		{
			warnings.Add("Skill does not declare allowed-tools; agents can request any tool while using it.");
		}

		var files = EnumerateStagedFiles(stagedDirectory);
		var hasScripts = files.Any(file => file.IsExecutable || file.RelativePath.StartsWith("scripts/", StringComparison.OrdinalIgnoreCase));
		if (hasScripts)
		{
			var scriptCount = files.Count(file =>
				file.IsExecutable || file.RelativePath.StartsWith("scripts/", StringComparison.OrdinalIgnoreCase));
			warnings.Add($"Skill contains {scriptCount} executable file(s). Review them before enabling the skill.");
		}

		var content = string.IsNullOrWhiteSpace(body) ? manifest.Trim() : body.Trim();
		if (string.IsNullOrWhiteSpace(content))
		{
			TryDeleteDirectory(stagedDirectory);
			throw new ValidationException("Marketplace skill content is empty.");
		}

		var preview = new SkillInstallPreview
		{
			Name = name,
			Description = description,
			Content = content,
			AllowedTools = allowedTools,
			IsEnabled = true,
			SourceType = SkillSourceType.Marketplace,
			SourceUri = $"{GitHubSkillCatalogClient.RepoOwner}/{GitHubSkillCatalogClient.RepoName}/{summary.Slug}",
			SourceRef = summary.Ref,
			HasScripts = hasScripts,
			Files = files,
			Warnings = warnings,
		};

		return (preview, stagedDirectory);
	}

	private async Task DownloadAndExtractFolderAsync(
		string slug,
		string gitRef,
		string destination,
		CancellationToken cancellationToken)
	{
		await using var tarballStream = await _catalogClient.DownloadTarballAsync(gitRef, cancellationToken);
		await using var gzipStream = new GZipStream(tarballStream, CompressionMode.Decompress);
		await using var reader = new TarReader(gzipStream, leaveOpen: false);

		// GitHub tarballs are prefixed with `<owner>-<repo>-<short-sha>/`, so the target folder
		// is at `<prefix>/<slug>/*`. We don't know the prefix up front — match on the segment
		// after the first slash instead.
		var extractedAny = false;
		while (await reader.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
		{
			if (!TryGetRelativePathInTargetFolder(entry.Name, slug, out var relativePath))
			{
				continue;
			}

			if (entry.EntryType == TarEntryType.Directory)
			{
				Directory.CreateDirectory(Path.Combine(destination, relativePath));
				continue;
			}

			if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
			{
				var outputPath = Path.Combine(destination, relativePath);
				Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
				await using var output = File.Create(outputPath);
				if (entry.DataStream is not null)
				{
					await entry.DataStream.CopyToAsync(output, cancellationToken);
				}

				// Preserve executable bit on Unix so scripts stay runnable post-install.
				if (!OperatingSystem.IsWindows() && (entry.Mode & UnixFileMode.UserExecute) != 0)
				{
					File.SetUnixFileMode(outputPath, entry.Mode);
				}

				extractedAny = true;
			}
		}

		if (!extractedAny)
		{
			throw new InvalidDataException($"Tarball did not contain a folder named '{slug}'.");
		}
	}

	private static bool TryGetRelativePathInTargetFolder(string entryName, string slug, out string relativePath)
	{
		// Normalize separators so both `foo/bar` and `foo\bar` match.
		var normalized = entryName.Replace('\\', '/');
		var firstSlash = normalized.IndexOf('/');
		if (firstSlash < 0 || firstSlash == normalized.Length - 1)
		{
			relativePath = string.Empty;
			return false;
		}

		var afterPrefix = normalized[(firstSlash + 1)..];
		if (!afterPrefix.StartsWith(slug + "/", StringComparison.OrdinalIgnoreCase) &&
			!string.Equals(afterPrefix, slug, StringComparison.OrdinalIgnoreCase))
		{
			relativePath = string.Empty;
			return false;
		}

		// "<prefix>/<slug>/..." → "..."
		if (afterPrefix.Length == slug.Length)
		{
			relativePath = string.Empty;
			return false; // Folder entry for slug root itself
		}

		relativePath = afterPrefix[(slug.Length + 1)..];
		return !string.IsNullOrEmpty(relativePath);
	}

	private static List<SkillFileEntry> EnumerateStagedFiles(string stagedDirectory)
	{
		var entries = new List<SkillFileEntry>();
		foreach (var file in Directory.EnumerateFiles(stagedDirectory, "*", SearchOption.AllDirectories))
		{
			var relative = Path.GetRelativePath(stagedDirectory, file).Replace('\\', '/');
			var info = new FileInfo(file);
			entries.Add(new SkillFileEntry
			{
				RelativePath = relative,
				Size = info.Length,
				IsExecutable = IsExecutable(file),
			});
		}

		return entries.OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
	}

	private static bool IsExecutable(string path)
	{
		if (OperatingSystem.IsWindows())
		{
			var extension = Path.GetExtension(path);
			return extension is ".exe" or ".bat" or ".cmd" or ".ps1" or ".sh";
		}

		try
		{
			var mode = File.GetUnixFileMode(path);
			return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
		}
		catch (IOException)
		{
			return false;
		}
	}

	private static string? NormalizeAllowedTools(string? raw)
	{
		if (string.IsNullOrWhiteSpace(raw))
		{
			return null;
		}

		return raw.Trim();
	}

	private static void TryDeleteDirectory(string path)
	{
		try
		{
			if (Directory.Exists(path))
			{
				Directory.Delete(path, recursive: true);
			}
		}
		catch
		{
			// best-effort
		}
	}
}
