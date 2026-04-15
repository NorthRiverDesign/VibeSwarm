using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Services;

/// <summary>
/// Default <see cref="ISkillStorageService"/> that writes skill folders under
/// <c>&lt;LocalApplicationData&gt;/VibeSwarm/skills/&lt;id&gt;/</c>, with a
/// <c>VIBESWARM_SKILLS_PATH</c> environment-variable override for container deployments.
/// </summary>
public class SkillStorageService : ISkillStorageService
{
	private const string SkillManifestFileName = "SKILL.md";
	private const string EnvironmentOverrideKey = "VIBESWARM_SKILLS_PATH";

	private readonly VibeSwarmDbContext _dbContext;
	private readonly ILogger<SkillStorageService> _logger;
	private readonly Lazy<string> _rootPath;

	public SkillStorageService(VibeSwarmDbContext dbContext, ILogger<SkillStorageService>? logger = null)
	{
		_dbContext = dbContext;
		_logger = logger ?? NullLogger<SkillStorageService>.Instance;
		_rootPath = new Lazy<string>(ResolveRootPath);
	}

	public string RootPath => _rootPath.Value;

	public string GetSkillDirectory(Guid skillId) => Path.Combine(RootPath, skillId.ToString("N"));

	public string GetSkillManifestPath(Guid skillId) =>
		Path.Combine(GetSkillDirectory(skillId), SkillManifestFileName);

	public async Task<string> EnsureMaterializedAsync(Skill skill, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(skill);

		var directory = GetSkillDirectory(skill.Id);
		var manifestPath = Path.Combine(directory, SkillManifestFileName);

		// Already materialized? Verify the on-disk state is consistent with the stored path.
		if (!string.IsNullOrWhiteSpace(skill.StoragePath) &&
			string.Equals(skill.StoragePath, directory, StringComparison.OrdinalIgnoreCase) &&
			File.Exists(manifestPath))
		{
			return directory;
		}

		Directory.CreateDirectory(directory);
		// Legacy skills have no folder; write their DB Content out as SKILL.md so agents can Read it.
		if (!File.Exists(manifestPath))
		{
			var content = string.IsNullOrWhiteSpace(skill.Content) ? string.Empty : skill.Content;
			await File.WriteAllTextAsync(manifestPath, content, cancellationToken);
			_logger.LogInformation("Materialized legacy skill {SkillId} ({SkillName}) to {Path}",
				skill.Id, skill.Name, manifestPath);
		}

		if (!string.Equals(skill.StoragePath, directory, StringComparison.OrdinalIgnoreCase))
		{
			skill.StoragePath = directory;
			// Only persist the change if this instance is tracked by the context. Callers that
			// materialize a detached skill (e.g. in tests) stay in charge of saving themselves.
			if (_dbContext.Entry(skill).State != EntityState.Detached)
			{
				await _dbContext.SaveChangesAsync(cancellationToken);
			}
		}

		return directory;
	}

	public async Task<string> MaterializeFromDirectoryAsync(Guid skillId, string sourceDirectory, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(sourceDirectory))
		{
			throw new ArgumentException("Source directory must be provided.", nameof(sourceDirectory));
		}

		if (!Directory.Exists(sourceDirectory))
		{
			throw new DirectoryNotFoundException($"Source skill directory not found: {sourceDirectory}");
		}

		var destination = GetSkillDirectory(skillId);
		if (Directory.Exists(destination))
		{
			Directory.Delete(destination, recursive: true);
		}

		Directory.CreateDirectory(destination);
		await CopyDirectoryAsync(sourceDirectory, destination, cancellationToken);
		return destination;
	}

	public void Delete(Guid skillId)
	{
		var directory = GetSkillDirectory(skillId);
		if (!Directory.Exists(directory))
		{
			return;
		}

		try
		{
			Directory.Delete(directory, recursive: true);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			_logger.LogWarning(ex, "Failed to delete skill storage directory {Path}", directory);
		}
	}

	private static string ResolveRootPath()
	{
		var overridePath = Environment.GetEnvironmentVariable(EnvironmentOverrideKey);
		string root;
		if (!string.IsNullOrWhiteSpace(overridePath))
		{
			root = Path.GetFullPath(overridePath);
		}
		else
		{
			var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
			// SpecialFolder.LocalApplicationData is empty on some Linux configurations; fall back
			// to $HOME/.local/share so skills still have a stable user-scoped location.
			if (string.IsNullOrWhiteSpace(appData))
			{
				var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
				if (string.IsNullOrWhiteSpace(home))
				{
					home = Environment.GetEnvironmentVariable("HOME") ?? Path.GetTempPath();
				}

				appData = Path.Combine(home, ".local", "share");
			}

			root = Path.Combine(appData, "VibeSwarm", "skills");
		}

		Directory.CreateDirectory(root);
		return root;
	}

	private static async Task CopyDirectoryAsync(string source, string destination, CancellationToken cancellationToken)
	{
		Directory.CreateDirectory(destination);

		foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
		{
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
		}
	}
}
