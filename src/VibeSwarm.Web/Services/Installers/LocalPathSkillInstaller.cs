using System.ComponentModel.DataAnnotations;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;

namespace VibeSwarm.Shared.Services;

/// <summary>
/// Installer strategy for a folder already sitting on the server's local filesystem. Expected
/// to contain <c>SKILL.md</c> at its root plus optional <c>references/</c> and <c>scripts/</c>
/// subtrees, matching the Claude-native skill layout.
/// </summary>
public sealed class LocalPathSkillInstaller : ISkillInstaller
{
	// Ceiling on the combined staged size. Keeps a typo (pointing at the user's home dir or
	// a repo checkout) from dragging gigabytes into the install preview.
	private const long MaxStagedSizeBytes = 50 * 1024 * 1024;

	public SkillInstallSource Source => SkillInstallSource.LocalPath;

	public async Task<(SkillInstallPreview Preview, string StagedDirectory)> StageAsync(
		SkillInstallRequest request,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		if (request.Source != SkillInstallSource.LocalPath)
		{
			throw new ArgumentException($"LocalPathSkillInstaller cannot handle source '{request.Source}'.", nameof(request));
		}

		var path = request.LocalPath?.Trim();
		if (string.IsNullOrWhiteSpace(path))
		{
			throw new ValidationException("Local folder path is required.");
		}

		string absolutePath;
		try
		{
			absolutePath = Path.GetFullPath(path);
		}
		catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
		{
			throw new ValidationException($"Invalid path: {ex.Message}");
		}

		if (!Directory.Exists(absolutePath))
		{
			throw new ValidationException($"Folder not found: {absolutePath}");
		}

		var manifestPath = Path.Combine(absolutePath, "SKILL.md");
		if (!File.Exists(manifestPath))
		{
			throw new ValidationException($"Folder does not contain SKILL.md: {absolutePath}");
		}

		var stagedDirectory = Path.Combine(Path.GetTempPath(), "vibeswarm", "skill-install", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(stagedDirectory);

		try
		{
			await CopyFolderAsync(absolutePath, stagedDirectory, cancellationToken);
		}
		catch
		{
			TryDeleteDirectory(stagedDirectory);
			throw;
		}

		var manifest = await File.ReadAllTextAsync(Path.Combine(stagedDirectory, "SKILL.md"), cancellationToken);
		var (metadata, body) = SkillManifestParser.ParseFrontMatter(manifest);
		var warnings = new List<string>();
		var fallbackName = new DirectoryInfo(absolutePath).Name;
		var name = SkillManifestParser.NormalizeName(SkillManifestParser.GetValue(metadata, "name"), fallbackName);
		var description = SkillManifestParser.NormalizeDescription(SkillManifestParser.GetValue(metadata, "description"), warnings);
		var allowedTools = NormalizeAllowedTools(SkillManifestParser.GetValue(metadata, "allowed-tools"));
		if (string.IsNullOrWhiteSpace(allowedTools))
		{
			warnings.Add("Skill does not declare allowed-tools; agents can request any tool while using it.");
		}

		var files = EnumerateStagedFiles(stagedDirectory);
		var totalBytes = files.Sum(file => file.Size);
		if (totalBytes > MaxStagedSizeBytes)
		{
			TryDeleteDirectory(stagedDirectory);
			throw new ValidationException(
				$"Folder exceeds the {MaxStagedSizeBytes / (1024 * 1024)} MB install limit " +
				$"(found {totalBytes / (1024 * 1024)} MB). Point at the skill folder itself, not a parent directory.");
		}

		var hasScripts = files.Any(file =>
			file.IsExecutable || file.RelativePath.StartsWith("scripts/", StringComparison.OrdinalIgnoreCase));
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
			throw new ValidationException("Skill content is empty.");
		}

		var preview = new SkillInstallPreview
		{
			Name = name,
			Description = description,
			Content = content,
			AllowedTools = allowedTools,
			IsEnabled = true,
			SourceType = SkillSourceType.LocalPath,
			SourceUri = absolutePath,
			SourceRef = null,
			HasScripts = hasScripts,
			Files = files,
			Warnings = warnings,
		};

		return (preview, stagedDirectory);
	}

	private static async Task CopyFolderAsync(string source, string destination, CancellationToken cancellationToken)
	{
		Directory.CreateDirectory(destination);
		foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var relative = Path.GetRelativePath(source, directory);
			Directory.CreateDirectory(Path.Combine(destination, relative));
		}

		foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var relative = Path.GetRelativePath(source, file);
			var destinationPath = Path.Combine(destination, relative);
			Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
			await using var sourceStream = File.OpenRead(file);
			await using var destinationStream = File.Create(destinationPath);
			await sourceStream.CopyToAsync(destinationStream, cancellationToken);

			// Preserve the executable bit on Unix so scripts remain runnable post-install.
			if (!OperatingSystem.IsWindows())
			{
				try
				{
					File.SetUnixFileMode(destinationPath, File.GetUnixFileMode(file));
				}
				catch (IOException)
				{
					// Permissions preservation is best-effort.
				}
			}
		}
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
		=> string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

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
