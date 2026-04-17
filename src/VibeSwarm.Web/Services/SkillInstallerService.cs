using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Validation;

namespace VibeSwarm.Shared.Services;

/// <summary>
/// Orchestrates skill installation: picks the strategy matching the request's source,
/// stages the files, and on commit copies them into central storage and writes the
/// <see cref="Skill"/> row with install metadata populated.
/// </summary>
public sealed class SkillInstallerService : ISkillInstallerService
{
	private readonly VibeSwarmDbContext _dbContext;
	private readonly ISkillStorageService _skillStorage;
	private readonly IReadOnlyDictionary<SkillInstallSource, ISkillInstaller> _installers;
	private readonly ILogger<SkillInstallerService> _logger;

	public SkillInstallerService(
		VibeSwarmDbContext dbContext,
		ISkillStorageService skillStorage,
		IEnumerable<ISkillInstaller> installers,
		ILogger<SkillInstallerService>? logger = null)
	{
		_dbContext = dbContext;
		_skillStorage = skillStorage;
		_logger = logger ?? NullLogger<SkillInstallerService>.Instance;

		// Last-registered-wins keeps test fixtures simple (they can override the default
		// strategy for a single source) without requiring container tricks.
		_installers = installers
			.GroupBy(installer => installer.Source)
			.ToDictionary(group => group.Key, group => group.Last());
	}

	public async Task<SkillInstallPreview> PreviewAsync(SkillInstallRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		var installer = ResolveInstaller(request.Source);
		var (preview, stagedDirectory) = await installer.StageAsync(request, cancellationToken);

		try
		{
			var nameExists = await _dbContext.Skills
				.AnyAsync(s => s.Name == preview.Name, cancellationToken);
			return preview with { NameExists = nameExists };
		}
		finally
		{
			TryDeleteDirectory(stagedDirectory);
		}
	}

	public async Task<SkillInstallResult> InstallAsync(SkillInstallRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		var installer = ResolveInstaller(request.Source);
		var (preview, stagedDirectory) = await installer.StageAsync(request, cancellationToken);

		try
		{
			var existing = await _dbContext.Skills
				.FirstOrDefaultAsync(s => s.Name == preview.Name, cancellationToken);
			if (existing is not null)
			{
				var skipped = preview with { NameExists = true };
				return new SkillInstallResult
				{
					Skipped = true,
					Message = $"Skill '{preview.Name}' already exists.",
					Skill = existing,
					Preview = skipped,
					Warnings = preview.Warnings,
				};
			}

			var skill = new Skill
			{
				Id = Guid.NewGuid(),
				Name = preview.Name,
				Description = preview.Description,
				Content = preview.Content,
				IsEnabled = preview.IsEnabled,
				SourceType = preview.SourceType,
				SourceUri = preview.SourceUri,
				SourceRef = preview.SourceRef,
				AllowedTools = preview.AllowedTools,
				HasScripts = preview.HasScripts,
				InstalledAt = DateTime.UtcNow,
				CreatedAt = DateTime.UtcNow,
			};

			ValidationHelper.ValidateObject(skill);

			var storagePath = await _skillStorage.MaterializeFromDirectoryAsync(skill.Id, stagedDirectory, cancellationToken);
			skill.StoragePath = storagePath;

			_dbContext.Skills.Add(skill);
			await _dbContext.SaveChangesAsync(cancellationToken);

			_logger.LogInformation(
				"Installed skill {SkillName} from source {Source} (id {SkillId}, storage {StoragePath})",
				skill.Name, preview.SourceType, skill.Id, storagePath);

			return new SkillInstallResult
			{
				Installed = true,
				Message = $"Installed skill '{skill.Name}'.",
				Skill = skill,
				Preview = preview with { NameExists = false },
				Warnings = preview.Warnings,
			};
		}
		finally
		{
			TryDeleteDirectory(stagedDirectory);
		}
	}

	private ISkillInstaller ResolveInstaller(SkillInstallSource source)
	{
		if (!_installers.TryGetValue(source, out var installer))
		{
			throw new InvalidOperationException($"No installer registered for source '{source}'.");
		}

		return installer;
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
			// Best-effort cleanup of the staging folder.
		}
	}
}
