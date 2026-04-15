using System.Data;
using System.Globalization;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Metadata;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Services;

public class DatabaseService : IDatabaseService
{
	private static readonly MethodInfo LoadEntitiesMethod = typeof(DatabaseService)
		.GetMethod(nameof(LoadEntitiesAsyncCore), BindingFlags.NonPublic | BindingFlags.Static)!;
	private static readonly MethodInfo AddEntitiesMethod = typeof(DatabaseService)
		.GetMethod(nameof(AddEntitiesAsyncCore), BindingFlags.NonPublic | BindingFlags.Static)!;
	private static readonly MethodInfo HasAnyEntitiesMethod = typeof(DatabaseService)
		.GetMethod(nameof(HasAnyEntitiesAsyncCore), BindingFlags.NonPublic | BindingFlags.Static)!;

	private readonly VibeSwarmDbContext _db;
	private readonly ICriticalErrorLogService _criticalErrorLogService;
	private readonly IDatabaseRuntimeConfigurationStore _runtimeConfigurationStore;

	public DatabaseService(
		VibeSwarmDbContext db,
		ICriticalErrorLogService criticalErrorLogService,
		IDatabaseRuntimeConfigurationStore runtimeConfigurationStore)
	{
		_db = db;
		_criticalErrorLogService = criticalErrorLogService;
		_runtimeConfigurationStore = runtimeConfigurationStore;
	}

	public async Task<DatabaseExportDto> ExportAsync(CancellationToken ct = default)
	{
		var settings = await _db.AppSettings.OrderBy(s => s.Id).FirstOrDefaultAsync(ct);

		var projects = await _db.Projects
			.Include(p => p.Ideas)
			.Include(p => p.Environments)
			.OrderBy(p => p.Name)
			.ToListAsync(ct);

		var skills = await _db.Skills
			.OrderBy(s => s.Name)
			.ToListAsync(ct);

		var agents = await _db.Agents
			.Include(r => r.SkillLinks)
				.ThenInclude(sl => sl.Skill)
			.OrderBy(r => r.Name)
			.ToListAsync(ct);

		var schedules = await _db.JobSchedules
			.Include(s => s.Project)
			.Include(s => s.Provider)
			.OrderBy(s => s.CreatedAt)
			.ToListAsync(ct);

		return new DatabaseExportDto
		{
			ExportedAt = DateTime.UtcNow,
			Settings = settings == null ? null : new AppSettingsExportDto
			{
				DefaultProjectsDirectory = settings.DefaultProjectsDirectory,
				TimeZoneId = settings.TimeZoneId,
				EnablePromptStructuring = settings.EnablePromptStructuring,
				InjectRepoMap = settings.InjectRepoMap,
				InjectEfficiencyRules = settings.InjectEfficiencyRules,
				EnableCommitAttribution = settings.EnableCommitAttribution,
				CriticalErrorLogRetentionDays = settings.CriticalErrorLogRetentionDays,
				CriticalErrorLogMaxEntries = settings.CriticalErrorLogMaxEntries,
			},
			Projects = projects.Select(p => new ProjectExportDto
			{
				Name = p.Name,
				Description = p.Description,
				WorkingPath = p.WorkingPath,
				GitHubRepository = p.GitHubRepository,
				AutoCommitMode = p.AutoCommitMode,
				GitChangeDeliveryMode = p.GitChangeDeliveryMode,
				DefaultTargetBranch = p.DefaultTargetBranch,
				PlanningEnabled = p.PlanningEnabled,
				PromptContext = p.PromptContext,
				IdeasAutoCommit = p.IdeasAutoCommit,
				IdeasProcessingProviderId = p.IdeasProcessingProviderId,
				IdeasProcessingModelId = p.IdeasProcessingModelId,
				EnableTeamSwarm = p.EnableTeamSwarm,
				BuildVerificationEnabled = p.BuildVerificationEnabled,
				BuildCommand = p.BuildCommand,
				TestCommand = p.TestCommand,
				IsActive = p.IsActive,
				Ideas = p.Ideas
					.OrderBy(i => i.SortOrder)
					.Select(i => new IdeaExportDto
					{
						Description = i.Description,
						ExpandedDescription = i.ExpandedDescription,
						ExpansionStatus = i.ExpansionStatus,
						SortOrder = i.SortOrder,
					}).ToList(),
				Environments = p.Environments
					.OrderBy(e => e.SortOrder)
					.Select(e => new EnvironmentExportDto
					{
						Name = e.Name,
						Description = e.Description,
						Url = e.Url,
						Type = e.Type,
						Stage = e.Stage,
						IsPrimary = e.IsPrimary,
						IsEnabled = e.IsEnabled,
						SortOrder = e.SortOrder,
					}).ToList(),
			}).ToList(),
			Skills = skills.Select(s => new SkillExportDto
			{
				Name = s.Name,
				Description = s.Description,
				Content = s.Content,
				IsEnabled = s.IsEnabled,
			}).ToList(),
			Agents = agents.Select(r => new AgentExportDto
			{
				Name = r.Name,
				Description = r.Description,
				Responsibilities = r.Responsibilities,
				DefaultModelId = r.DefaultModelId,
				IsEnabled = r.IsEnabled,
				SkillNames = r.SkillLinks
					.Where(sl => sl.Skill != null)
					.Select(sl => sl.Skill!.Name)
					.OrderBy(n => n)
					.ToList(),
			}).ToList(),
			Schedules = schedules
				.Where(s => s.Project != null && s.Provider != null)
				.Select(s => new ScheduleExportDto
				{
					ProjectName = s.Project!.Name,
					ProviderName = s.Provider!.Name,
					Prompt = s.Prompt,
					ModelId = s.ModelId,
					Frequency = s.Frequency,
					HourUtc = s.HourUtc,
					MinuteUtc = s.MinuteUtc,
					WeeklyDay = s.WeeklyDay,
					DayOfMonth = s.DayOfMonth,
					IsEnabled = s.IsEnabled,
				}).ToList(),
		};
	}

	public async Task<DatabaseImportResult> ImportAsync(DatabaseExportDto export, CancellationToken ct = default)
	{
		var result = new DatabaseImportResult();

		// Import skills first (referenced by team roles)
		var existingSkillNames = await _db.Skills.Select(s => s.Name).ToHashSetAsync(ct);
		var importedSkillsByName = new Dictionary<string, Skill>(StringComparer.OrdinalIgnoreCase);

		foreach (var skillDto in export.Skills)
		{
			if (string.IsNullOrWhiteSpace(skillDto.Name))
				continue;

			if (existingSkillNames.Contains(skillDto.Name))
			{
				result.Skipped.Add($"Skill '{skillDto.Name}' already exists");
				var existing = await _db.Skills.FirstAsync(s => s.Name == skillDto.Name, ct);
				importedSkillsByName[skillDto.Name] = existing;
			}
			else
			{
				var skill = new Skill
				{
					Id = Guid.NewGuid(),
					Name = skillDto.Name,
					Description = skillDto.Description,
					Content = skillDto.Content,
					IsEnabled = skillDto.IsEnabled,
					CreatedAt = DateTime.UtcNow,
				};
				_db.Skills.Add(skill);
				importedSkillsByName[skillDto.Name] = skill;
				result.Imported.Add($"Skill '{skillDto.Name}'");
			}
		}

		await _db.SaveChangesAsync(ct);

		// Import team roles
		var existingRoleNames = await _db.Agents.Select(r => r.Name).ToHashSetAsync(ct);

		foreach (var roleDto in export.Agents)
		{
			if (string.IsNullOrWhiteSpace(roleDto.Name))
				continue;

			if (existingRoleNames.Contains(roleDto.Name))
			{
				result.Skipped.Add($"Team role '{roleDto.Name}' already exists");
				continue;
			}

			var role = new Agent
			{
				Id = Guid.NewGuid(),
				Name = roleDto.Name,
				Description = roleDto.Description,
				Responsibilities = roleDto.Responsibilities,
				DefaultModelId = roleDto.DefaultModelId,
				IsEnabled = roleDto.IsEnabled,
				CreatedAt = DateTime.UtcNow,
			};

			foreach (var skillName in roleDto.SkillNames)
			{
				if (importedSkillsByName.TryGetValue(skillName, out var skill))
				{
					role.SkillLinks.Add(new AgentSkill { AgentId = role.Id, SkillId = skill.Id });
				}
				else
				{
					var existingSkill = await _db.Skills.FirstOrDefaultAsync(s => s.Name == skillName, ct);
					if (existingSkill != null)
						role.SkillLinks.Add(new AgentSkill { AgentId = role.Id, SkillId = existingSkill.Id });
				}
			}

			_db.Agents.Add(role);
			result.Imported.Add($"Team role '{roleDto.Name}'");
		}

		await _db.SaveChangesAsync(ct);

		// Import projects (with ideas and environments)
		var existingProjectNames = await _db.Projects.Select(p => p.Name).ToHashSetAsync(ct);

		foreach (var projectDto in export.Projects)
		{
			if (string.IsNullOrWhiteSpace(projectDto.Name))
				continue;

			if (existingProjectNames.Contains(projectDto.Name))
			{
				result.Skipped.Add($"Project '{projectDto.Name}' already exists");

				// Still check for new ideas on the existing project
				var existingProject = await _db.Projects
					.Include(p => p.Ideas)
					.FirstAsync(p => p.Name == projectDto.Name, ct);

				var existingDescriptions = existingProject.Ideas
					.Select(i => i.Description)
					.ToHashSet(StringComparer.Ordinal);

				var newIdeas = projectDto.Ideas
					.Where(i => !string.IsNullOrWhiteSpace(i.Description) && !existingDescriptions.Contains(i.Description))
					.ToList();

				foreach (var ideaDto in newIdeas)
				{
					_db.Ideas.Add(new Idea
					{
						Id = Guid.NewGuid(),
						ProjectId = existingProject.Id,
						Description = ideaDto.Description,
						ExpandedDescription = ideaDto.ExpandedDescription,
						ExpansionStatus = ideaDto.ExpansionStatus,
						SortOrder = ideaDto.SortOrder,
						CreatedAt = DateTime.UtcNow,
					});
					result.Imported.Add($"Idea '{TruncateLabel(ideaDto.Description)}' for project '{projectDto.Name}'");
				}

				continue;
			}

			var project = new Project
			{
				Id = Guid.NewGuid(),
				Name = projectDto.Name,
				Description = projectDto.Description,
				WorkingPath = projectDto.WorkingPath,
				GitHubRepository = projectDto.GitHubRepository,
				AutoCommitMode = projectDto.AutoCommitMode,
				GitChangeDeliveryMode = projectDto.GitChangeDeliveryMode,
				DefaultTargetBranch = projectDto.DefaultTargetBranch,
				PlanningEnabled = projectDto.PlanningEnabled,
				PromptContext = projectDto.PromptContext,
				IdeasAutoCommit = projectDto.IdeasAutoCommit,
				IdeasProcessingProviderId = projectDto.IdeasProcessingProviderId,
				IdeasProcessingModelId = projectDto.IdeasProcessingModelId,
				EnableTeamSwarm = projectDto.EnableTeamSwarm,
				BuildVerificationEnabled = projectDto.BuildVerificationEnabled,
				BuildCommand = projectDto.BuildCommand,
				TestCommand = projectDto.TestCommand,
				IsActive = projectDto.IsActive,
				CreatedAt = DateTime.UtcNow,
			};

			foreach (var ideaDto in projectDto.Ideas.Where(i => !string.IsNullOrWhiteSpace(i.Description)))
			{
				project.Ideas.Add(new Idea
				{
					Id = Guid.NewGuid(),
					ProjectId = project.Id,
					Description = ideaDto.Description,
					ExpandedDescription = ideaDto.ExpandedDescription,
					ExpansionStatus = ideaDto.ExpansionStatus,
					SortOrder = ideaDto.SortOrder,
					CreatedAt = DateTime.UtcNow,
				});
			}

			foreach (var envDto in projectDto.Environments.Where(e => !string.IsNullOrWhiteSpace(e.Name)))
			{
				project.Environments.Add(new ProjectEnvironment
				{
					Id = Guid.NewGuid(),
					ProjectId = project.Id,
					Name = envDto.Name,
					Description = envDto.Description,
					Url = envDto.Url,
					Type = envDto.Type,
					Stage = envDto.Stage,
					IsPrimary = envDto.IsPrimary,
					IsEnabled = envDto.IsEnabled,
					SortOrder = envDto.SortOrder,
					CreatedAt = DateTime.UtcNow,
				});
			}

			_db.Projects.Add(project);
			result.Imported.Add($"Project '{projectDto.Name}' ({projectDto.Ideas.Count} ideas, {projectDto.Environments.Count} environments)");
		}

		await _db.SaveChangesAsync(ct);

		// Import schedules (require matching project and provider by name)
		var projectsByName = await _db.Projects.ToDictionaryAsync(p => p.Name, p => p, ct);
		var providersByName = await _db.Providers.ToDictionaryAsync(p => p.Name, p => p, ct);

		foreach (var scheduleDto in export.Schedules)
		{
			if (!projectsByName.TryGetValue(scheduleDto.ProjectName, out var project))
			{
				result.Errors.Add($"Schedule skipped: project '{scheduleDto.ProjectName}' not found");
				continue;
			}

			if (!providersByName.TryGetValue(scheduleDto.ProviderName, out var provider))
			{
				result.Errors.Add($"Schedule skipped: provider '{scheduleDto.ProviderName}' not found");
				continue;
			}

			var duplicate = await _db.JobSchedules.AnyAsync(s =>
				s.ProjectId == project.Id &&
				s.ProviderId == provider.Id &&
				s.Prompt == scheduleDto.Prompt &&
				s.Frequency == scheduleDto.Frequency, ct);

			if (duplicate)
			{
				result.Skipped.Add($"Schedule for '{scheduleDto.ProjectName}' ({scheduleDto.Frequency}) already exists");
				continue;
			}

			var schedule = new JobSchedule
			{
				Id = Guid.NewGuid(),
				ProjectId = project.Id,
				ProviderId = provider.Id,
				Prompt = scheduleDto.Prompt,
				ModelId = scheduleDto.ModelId,
				Frequency = scheduleDto.Frequency,
				HourUtc = scheduleDto.HourUtc,
				MinuteUtc = scheduleDto.MinuteUtc,
				WeeklyDay = scheduleDto.WeeklyDay,
				DayOfMonth = scheduleDto.DayOfMonth,
				IsEnabled = scheduleDto.IsEnabled,
				CreatedAt = DateTime.UtcNow,
				NextRunAtUtc = DateTime.UtcNow,
			};

			_db.JobSchedules.Add(schedule);
			result.Imported.Add($"Schedule for '{scheduleDto.ProjectName}' ({scheduleDto.Frequency})");
		}

		await _db.SaveChangesAsync(ct);

		// Import settings (optional — only if present in export)
		if (export.Settings != null)
		{
			var existing = await _db.AppSettings.OrderBy(s => s.Id).FirstOrDefaultAsync(ct);
			if (existing != null)
			{
				result.Skipped.Add("App settings already configured — not overwritten");
			}
			else
			{
				_db.AppSettings.Add(new AppSettings
				{
					Id = Guid.NewGuid(),
					DefaultProjectsDirectory = export.Settings.DefaultProjectsDirectory,
					TimeZoneId = export.Settings.TimeZoneId ?? "UTC",
					EnablePromptStructuring = export.Settings.EnablePromptStructuring,
					InjectRepoMap = export.Settings.InjectRepoMap,
					InjectEfficiencyRules = export.Settings.InjectEfficiencyRules,
					EnableCommitAttribution = export.Settings.EnableCommitAttribution,
					CriticalErrorLogRetentionDays = export.Settings.CriticalErrorLogRetentionDays,
					CriticalErrorLogMaxEntries = export.Settings.CriticalErrorLogMaxEntries,
					UpdatedAt = DateTime.UtcNow,
				});
				await _db.SaveChangesAsync(ct);
				result.Imported.Add("App settings");
			}
		}

		return result;
	}

	public Task<DatabaseConfigurationInfo> GetConfigurationAsync(CancellationToken ct = default)
	{
		var currentProvider = GetProvider();
		var currentConnectionString = _db.Database.GetConnectionString();
		var runtimeConfiguration = _runtimeConfigurationStore.Load();
		var hasEnvironmentOverride = HasEnvironmentOverride();
		var pendingConfiguration = GetPendingConfiguration(runtimeConfiguration, currentProvider, currentConnectionString);

		return Task.FromResult(new DatabaseConfigurationInfo
		{
			Provider = currentProvider,
			ConnectionStringPreview = BuildConnectionStringPreview(currentProvider, currentConnectionString),
			ConfigurationSource = hasEnvironmentOverride
				? "Environment variables"
				: runtimeConfiguration != null
					? "Runtime database config file"
					: "Application configuration",
			RuntimeConfigurationPath = _runtimeConfigurationStore.ConfigurationPath,
			HasEnvironmentOverride = hasEnvironmentOverride,
			CanUpdateConfiguration = !hasEnvironmentOverride,
			PendingProvider = pendingConfiguration?.Provider,
			PendingConnectionStringPreview = pendingConfiguration == null
				? null
				: BuildConnectionStringPreview(pendingConfiguration.Provider ?? currentProvider, pendingConfiguration.ConnectionString)
		});
	}

	public async Task<DatabaseStorageSummary> GetStorageSummaryAsync(CancellationToken ct = default)
	{
		var provider = GetProvider();
		var retentionCutoff = DateTime.UtcNow.AddDays(-DatabaseMaintenanceDefaults.HistoryRetentionDays);
		var sizeDetails = await GetCurrentSizeDetailsAsync(provider, ct);

		return new DatabaseStorageSummary
		{
			Provider = provider,
			Location = GetDatabaseLocation(provider),
			TotalSizeBytes = sizeDetails.TotalSizeBytes,
			DataFileSizeBytes = sizeDetails.DataFileSizeBytes,
			WalFileSizeBytes = sizeDetails.WalFileSizeBytes,
			SharedMemoryFileSizeBytes = sizeDetails.SharedMemoryFileSizeBytes,
			JobsCount = await _db.Jobs.CountAsync(ct),
			JobMessagesCount = await _db.JobMessages.CountAsync(ct),
			ProviderUsageRecordsCount = await _db.ProviderUsageRecords.CountAsync(ct),
			CriticalErrorLogsCount = await _db.CriticalErrorLogs.CountAsync(ct),
			CompletedJobsOlderThanRetentionCount = await _db.Jobs.CountAsync(
				job => job.Status == JobStatus.Completed && (job.CompletedAt ?? job.CreatedAt) < retentionCutoff,
				ct),
			ProviderUsageRecordsOlderThanRetentionCount = await _db.ProviderUsageRecords.CountAsync(
				record => record.RecordedAt < retentionCutoff,
				ct),
			SupportsCompaction = string.Equals(provider, "sqlite", StringComparison.OrdinalIgnoreCase),
			MeasuredAtUtc = DateTime.UtcNow
		};
	}

	public async Task<DatabaseMigrationResult> MigrateAsync(DatabaseMigrationRequest request, CancellationToken ct = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		if (HasEnvironmentOverride())
		{
			throw new InvalidOperationException(
				"Database migration is disabled while DATABASE_PROVIDER or ConnectionStrings__Default is set as an environment variable. " +
				"Clear the environment override and try again.");
		}

		var targetProvider = DataServiceExtensions.ResolveProviderName(request.Provider);
		var targetConnectionString = NormalizeTargetConnectionString(targetProvider, request.ConnectionString);
		var currentProvider = GetProvider();
		var currentConnectionString = _db.Database.GetConnectionString();

		if (MatchesCurrentDatabase(targetProvider, targetConnectionString, currentProvider, currentConnectionString))
		{
			throw new InvalidOperationException("Choose a different target database before starting a migration.");
		}

		var targetOptions = new DbContextOptionsBuilder<VibeSwarmDbContext>();
		DataServiceExtensions.ConfigureDbContext(targetOptions, targetConnectionString, targetProvider);

		await using var targetDb = new VibeSwarmDbContext(targetOptions.Options);
		await targetDb.Database.MigrateAsync(ct);

		if (await HasExistingApplicationDataAsync(targetDb, ct))
		{
			throw new InvalidOperationException(
				"The target database already contains VibeSwarm data. Choose an empty database for migration.");
		}

		var copySummary = await CopyAllDataAsync(_db, targetDb, ct);
		await _runtimeConfigurationStore.SaveAsync(new DatabaseRuntimeConfiguration
		{
			Provider = targetProvider,
			ConnectionString = targetConnectionString
		}, ct);

		return new DatabaseMigrationResult
		{
			Provider = targetProvider,
			ConnectionStringPreview = BuildConnectionStringPreview(targetProvider, targetConnectionString),
			CopiedTableCount = copySummary.TableCount,
			CopiedRowCount = copySummary.RowCount,
			Message = $"Copied {copySummary.RowCount} row(s) across {copySummary.TableCount} table(s). Restart VibeSwarm to begin using the {GetProviderDisplayName(targetProvider)} database."
		};
	}

	public async Task<DatabaseMaintenanceResult> RunMaintenanceAsync(DatabaseMaintenanceRequest request, CancellationToken ct = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		var provider = GetProvider();
		var sizeBefore = await GetCurrentSizeDetailsAsync(provider, ct);
		var result = request.Operation switch
		{
			DatabaseMaintenanceOperation.ApplyCriticalErrorLogRetention => await ApplyCriticalErrorLogRetentionAsync(ct),
			DatabaseMaintenanceOperation.DeleteCompletedJobsOlderThanRetention => await DeleteCompletedJobsOlderThanRetentionAsync(ct),
			DatabaseMaintenanceOperation.DeleteProviderUsageOlderThanRetention => await DeleteProviderUsageOlderThanRetentionAsync(ct),
			DatabaseMaintenanceOperation.CompactDatabase => await CompactDatabaseAsync(provider, ct),
			_ => throw new InvalidOperationException($"Unsupported maintenance operation '{request.Operation}'.")
		};

		var sizeAfter = await GetCurrentSizeDetailsAsync(provider, ct);
		result.Operation = request.Operation;
		result.SizeBeforeBytes = sizeBefore.TotalSizeBytes;
		result.SizeAfterBytes = sizeAfter.TotalSizeBytes;
		return result;
	}

	private static string TruncateLabel(string text, int maxLength = 50)
		=> text.Length <= maxLength ? text : text[..maxLength] + "…";

	private async Task<DatabaseMaintenanceResult> ApplyCriticalErrorLogRetentionAsync(CancellationToken ct)
	{
		var beforeCount = await _db.CriticalErrorLogs.CountAsync(ct);
		await _criticalErrorLogService.ApplyRetentionPolicyAsync(ct);
		var afterCount = await _db.CriticalErrorLogs.CountAsync(ct);
		var removedCount = Math.Max(beforeCount - afterCount, 0);

		return new DatabaseMaintenanceResult
		{
			AffectedRows = removedCount,
			Message = removedCount == 0
				? "Critical error logs already match the configured retention policy."
				: $"Removed {removedCount} critical error log entr{(removedCount == 1 ? "y" : "ies")}."
		};
	}

	private async Task<DatabaseMaintenanceResult> DeleteCompletedJobsOlderThanRetentionAsync(CancellationToken ct)
	{
		var retentionCutoff = DateTime.UtcNow.AddDays(-DatabaseMaintenanceDefaults.HistoryRetentionDays);
		var jobs = await _db.Jobs
			.Where(job => job.Status == JobStatus.Completed && (job.CompletedAt ?? job.CreatedAt) < retentionCutoff)
			.ToListAsync(ct);

		if (jobs.Count == 0)
		{
			return new DatabaseMaintenanceResult
			{
				Message = $"No completed jobs older than {DatabaseMaintenanceDefaults.HistoryRetentionDays} days were found."
			};
		}

		_db.Jobs.RemoveRange(jobs);
		await _db.SaveChangesAsync(ct);

		return new DatabaseMaintenanceResult
		{
			AffectedRows = jobs.Count,
			Message = $"Deleted {jobs.Count} completed job{(jobs.Count == 1 ? string.Empty : "s")} older than {DatabaseMaintenanceDefaults.HistoryRetentionDays} days."
		};
	}

	private async Task<DatabaseMaintenanceResult> DeleteProviderUsageOlderThanRetentionAsync(CancellationToken ct)
	{
		var retentionCutoff = DateTime.UtcNow.AddDays(-DatabaseMaintenanceDefaults.HistoryRetentionDays);
		var records = await _db.ProviderUsageRecords
			.Where(record => record.RecordedAt < retentionCutoff)
			.ToListAsync(ct);

		if (records.Count == 0)
		{
			return new DatabaseMaintenanceResult
			{
				Message = $"No provider usage records older than {DatabaseMaintenanceDefaults.HistoryRetentionDays} days were found."
			};
		}

		_db.ProviderUsageRecords.RemoveRange(records);
		await _db.SaveChangesAsync(ct);

		return new DatabaseMaintenanceResult
		{
			AffectedRows = records.Count,
			Message = $"Deleted {records.Count} provider usage record{(records.Count == 1 ? string.Empty : "s")} older than {DatabaseMaintenanceDefaults.HistoryRetentionDays} days."
		};
	}

	private async Task<DatabaseMaintenanceResult> CompactDatabaseAsync(string provider, CancellationToken ct)
	{
		if (!string.Equals(provider, "sqlite", StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException("Database compaction is currently supported only for SQLite databases.");
		}

		await ExecuteNonQueryAsync("PRAGMA wal_checkpoint(TRUNCATE);", ct);
		await ExecuteNonQueryAsync("VACUUM;", ct);

		return new DatabaseMaintenanceResult
		{
			Message = "Compacted the SQLite database file and truncated the WAL where possible."
		};
	}

	private string GetProvider()
	{
		var providerName = _db.Database.ProviderName ?? string.Empty;

		if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
		{
			return "sqlite";
		}

		if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
		{
			return "postgresql";
		}

		if (providerName.Contains("MySql", StringComparison.OrdinalIgnoreCase) ||
			providerName.Contains("MariaDb", StringComparison.OrdinalIgnoreCase))
		{
			return "mysql";
		}

		if (providerName.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
		{
			return "sqlserver";
		}

		return string.IsNullOrWhiteSpace(providerName) ? "unknown" : providerName;
	}

	private string? GetDatabaseLocation(string provider)
	{
		if (!string.Equals(provider, "sqlite", StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}

		return GetSqliteDatabasePath() ?? "In-memory SQLite";
	}

	private async Task<DatabaseSizeDetails> GetCurrentSizeDetailsAsync(string provider, CancellationToken ct)
	{
		if (string.Equals(provider, "sqlite", StringComparison.OrdinalIgnoreCase))
		{
			return await GetSqliteSizeDetailsAsync(ct);
		}

		var sizeBytes = provider switch
		{
			"mysql" => await ExecuteScalarInt64Async(
				"SELECT COALESCE(SUM(data_length + index_length), 0) FROM information_schema.tables WHERE table_schema = DATABASE()",
				ct),
			"postgresql" => await ExecuteScalarInt64Async("SELECT pg_database_size(current_database())", ct),
			"sqlserver" => await ExecuteScalarInt64Async("SELECT SUM(CAST(size AS BIGINT)) * 8192 FROM sys.database_files", ct),
			_ => null
		};

		return new DatabaseSizeDetails
		{
			TotalSizeBytes = sizeBytes
		};
	}

	private async Task<DatabaseSizeDetails> GetSqliteSizeDetailsAsync(CancellationToken ct)
	{
		var dataSourcePath = GetSqliteDatabasePath();
		if (!string.IsNullOrWhiteSpace(dataSourcePath))
		{
			var dataFile = new FileInfo(dataSourcePath);
			var walFile = new FileInfo($"{dataSourcePath}-wal");
			var sharedMemoryFile = new FileInfo($"{dataSourcePath}-shm");
			var dataFileBytes = dataFile.Exists ? dataFile.Length : 0;
			var walFileBytes = walFile.Exists ? walFile.Length : 0;
			var sharedMemoryFileBytes = sharedMemoryFile.Exists ? sharedMemoryFile.Length : 0;

			return new DatabaseSizeDetails
			{
				DataFileSizeBytes = dataFileBytes,
				WalFileSizeBytes = walFile.Exists ? walFileBytes : null,
				SharedMemoryFileSizeBytes = sharedMemoryFile.Exists ? sharedMemoryFileBytes : null,
				TotalSizeBytes = dataFileBytes + walFileBytes + sharedMemoryFileBytes
			};
		}

		var pageSize = await ExecuteScalarInt64Async("PRAGMA page_size;", ct) ?? 0;
		var pageCount = await ExecuteScalarInt64Async("PRAGMA page_count;", ct) ?? 0;
		return new DatabaseSizeDetails
		{
			TotalSizeBytes = pageSize * pageCount
		};
	}

	private string? GetSqliteDatabasePath()
	{
		var connectionString = _db.Database.GetConnectionString();
		if (string.IsNullOrWhiteSpace(connectionString))
		{
			return null;
		}

		var builder = new SqliteConnectionStringBuilder(connectionString);
		if (string.IsNullOrWhiteSpace(builder.DataSource) ||
			string.Equals(builder.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase) ||
			builder.Mode == SqliteOpenMode.Memory)
		{
			return null;
		}

		return Path.GetFullPath(builder.DataSource);
	}

	private async Task<long?> ExecuteScalarInt64Async(string sql, CancellationToken ct)
	{
		var result = await ExecuteScalarAsync(sql, ct);
		if (result == null || result is DBNull)
		{
			return null;
		}

		return Convert.ToInt64(result, CultureInfo.InvariantCulture);
	}

	private async Task<object?> ExecuteScalarAsync(string sql, CancellationToken ct)
	{
		var connection = _db.Database.GetDbConnection();
		var shouldClose = connection.State != ConnectionState.Open;

		if (shouldClose)
		{
			await connection.OpenAsync(ct);
		}

		try
		{
			await using var command = connection.CreateCommand();
			command.CommandText = sql;
			return await command.ExecuteScalarAsync(ct);
		}
		finally
		{
			if (shouldClose)
			{
				await connection.CloseAsync();
			}
		}
	}

	private async Task ExecuteNonQueryAsync(string sql, CancellationToken ct)
	{
		var connection = _db.Database.GetDbConnection();
		var shouldClose = connection.State != ConnectionState.Open;

		if (shouldClose)
		{
			await connection.OpenAsync(ct);
		}

		try
		{
			await using var command = connection.CreateCommand();
			command.CommandText = sql;
			await command.ExecuteNonQueryAsync(ct);
		}
		finally
		{
			if (shouldClose)
			{
				await connection.CloseAsync();
			}
		}
	}

	private sealed class DatabaseSizeDetails
	{
		public long? TotalSizeBytes { get; set; }
		public long? DataFileSizeBytes { get; set; }
		public long? WalFileSizeBytes { get; set; }
		public long? SharedMemoryFileSizeBytes { get; set; }
	}

	private static string GetProviderDisplayName(string provider)
	{
		return provider switch
		{
			"sqlite" => "SQLite",
			"mysql" => "MySQL",
			"postgresql" => "PostgreSQL",
			"sqlserver" => "SQL Server",
			_ => provider
		};
	}

	private static bool HasEnvironmentOverride()
	{
		return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DATABASE_PROVIDER")) ||
			!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ConnectionStrings__Default")) ||
			!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CONNECTIONSTRINGS__DEFAULT"));
	}

	private static DatabaseRuntimeConfiguration? GetPendingConfiguration(
		DatabaseRuntimeConfiguration? runtimeConfiguration,
		string currentProvider,
		string? currentConnectionString)
	{
		if (runtimeConfiguration == null ||
			string.IsNullOrWhiteSpace(runtimeConfiguration.Provider) ||
			string.IsNullOrWhiteSpace(runtimeConfiguration.ConnectionString))
		{
			return null;
		}

		var pendingProvider = DataServiceExtensions.ResolveProviderName(runtimeConfiguration.Provider);
		return MatchesCurrentDatabase(
			pendingProvider,
			runtimeConfiguration.ConnectionString,
			currentProvider,
			currentConnectionString)
			? null
			: runtimeConfiguration;
	}

	private static string BuildConnectionStringPreview(string provider, string? connectionString)
	{
		if (string.IsNullOrWhiteSpace(connectionString))
		{
			return "Not configured";
		}

		if (string.Equals(provider, "sqlite", StringComparison.OrdinalIgnoreCase))
		{
			try
			{
				var builder = new SqliteConnectionStringBuilder(connectionString);
				if (string.IsNullOrWhiteSpace(builder.DataSource))
				{
					return "SQLite";
				}

				return string.Equals(builder.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase)
					? "In-memory SQLite"
					: Path.GetFullPath(builder.DataSource);
			}
			catch
			{
				return "SQLite";
			}
		}

		try
		{
			var builder = new System.Data.Common.DbConnectionStringBuilder
			{
				ConnectionString = connectionString
			};

			foreach (var key in builder.Keys.OfType<string>().ToList())
			{
				if (IsSecretConnectionStringKey(key))
				{
					builder[key] = "********";
				}
			}

			return builder.ConnectionString;
		}
		catch
		{
			return "Configured";
		}
	}

	private static bool IsSecretConnectionStringKey(string key)
	{
		return key.Equals("Password", StringComparison.OrdinalIgnoreCase) ||
			key.Equals("Pwd", StringComparison.OrdinalIgnoreCase) ||
			key.Equals("User ID", StringComparison.OrdinalIgnoreCase) ||
			key.Equals("Uid", StringComparison.OrdinalIgnoreCase) ||
			key.Equals("Username", StringComparison.OrdinalIgnoreCase);
	}

	private static string NormalizeTargetConnectionString(string provider, string? connectionString)
	{
		var trimmedConnectionString = connectionString?.Trim();
		if (string.IsNullOrWhiteSpace(trimmedConnectionString))
		{
			throw new InvalidOperationException("Enter a target connection string before starting a migration.");
		}

		if (!string.Equals(provider, "sqlite", StringComparison.OrdinalIgnoreCase))
		{
			return trimmedConnectionString;
		}

		var builder = new SqliteConnectionStringBuilder(trimmedConnectionString);
		if (string.IsNullOrWhiteSpace(builder.DataSource) ||
			string.Equals(builder.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase) ||
			builder.Mode == SqliteOpenMode.Memory)
		{
			throw new InvalidOperationException("Choose a file-based SQLite connection string for the migration target.");
		}

		builder.DataSource = Path.GetFullPath(builder.DataSource);
		var directory = Path.GetDirectoryName(builder.DataSource);
		if (!string.IsNullOrWhiteSpace(directory))
		{
			Directory.CreateDirectory(directory);
		}

		return builder.ToString();
	}

	private static bool MatchesCurrentDatabase(
		string targetProvider,
		string? targetConnectionString,
		string currentProvider,
		string? currentConnectionString)
	{
		if (!string.Equals(targetProvider, currentProvider, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		return string.Equals(
			NormalizeConnectionStringForComparison(targetProvider, targetConnectionString),
			NormalizeConnectionStringForComparison(currentProvider, currentConnectionString),
			StringComparison.OrdinalIgnoreCase);
	}

	private static string NormalizeConnectionStringForComparison(string provider, string? connectionString)
	{
		if (string.IsNullOrWhiteSpace(connectionString))
		{
			return string.Empty;
		}

		if (!string.Equals(provider, "sqlite", StringComparison.OrdinalIgnoreCase))
		{
			return connectionString.Trim();
		}

		try
		{
			var builder = new SqliteConnectionStringBuilder(connectionString);
			if (!string.IsNullOrWhiteSpace(builder.DataSource) &&
				!string.Equals(builder.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase) &&
				builder.Mode != SqliteOpenMode.Memory)
			{
				builder.DataSource = Path.GetFullPath(builder.DataSource);
			}

			return builder.ToString();
		}
		catch
		{
			return connectionString.Trim();
		}
	}

	private static IEnumerable<IEntityType> GetOrderedEntityTypes(VibeSwarmDbContext dbContext)
	{
		var entityTypes = dbContext.Model.GetEntityTypes()
			.Where(entityType =>
				!entityType.IsOwned() &&
				entityType.FindPrimaryKey() != null &&
				entityType.ClrType.IsClass &&
				entityType.GetTableName() != null)
			.OrderBy(entityType => entityType.Name, StringComparer.Ordinal)
			.ToList();
		var entityTypeSet = entityTypes.ToHashSet();
		var remaining = new HashSet<IEntityType>(entityTypes);
		var ordered = new List<IEntityType>(entityTypes.Count);

		while (remaining.Count > 0)
		{
			var ready = remaining
				.Where(entityType => entityType
					.GetForeignKeys()
					.Where(foreignKey => !foreignKey.IsOwnership && foreignKey.PrincipalEntityType != entityType)
					.Select(foreignKey => foreignKey.PrincipalEntityType)
					.Where(entityTypeSet.Contains)
					.All(principal => !remaining.Contains(principal)))
				.OrderBy(entityType => entityType.Name, StringComparer.Ordinal)
				.ToList();

			if (ready.Count == 0)
			{
				ready = remaining
					.OrderBy(entityType => entityType.Name, StringComparer.Ordinal)
					.ToList();
			}

			foreach (var entityType in ready)
			{
				ordered.Add(entityType);
				remaining.Remove(entityType);
			}
		}

		return ordered;
	}

	private static async Task<(int TableCount, int RowCount)> CopyAllDataAsync(
		VibeSwarmDbContext sourceDb,
		VibeSwarmDbContext targetDb,
		CancellationToken ct)
	{
		var entityTypes = GetOrderedEntityTypes(sourceDb);
		var copiedTableCount = 0;
		var copiedRowCount = 0;

		await using var transaction = await targetDb.Database.BeginTransactionAsync(ct);
		targetDb.ChangeTracker.AutoDetectChangesEnabled = false;

		try
		{
			foreach (var entityType in entityTypes)
			{
				var clones = await CloneEntitiesAsync(sourceDb, entityType, ct);
				if (clones.Count == 0)
				{
					continue;
				}

				await AddEntitiesAsync(targetDb, entityType.ClrType, clones, ct);
				copiedTableCount++;
				copiedRowCount += clones.Count;
			}

			await transaction.CommitAsync(ct);
			return (copiedTableCount, copiedRowCount);
		}
		catch
		{
			await transaction.RollbackAsync(ct);
			throw;
		}
		finally
		{
			targetDb.ChangeTracker.AutoDetectChangesEnabled = true;
		}
	}

	private static async Task<bool> HasExistingApplicationDataAsync(VibeSwarmDbContext dbContext, CancellationToken ct)
	{
		foreach (var entityType in GetOrderedEntityTypes(dbContext))
		{
			if (await HasAnyEntitiesAsync(dbContext, entityType.ClrType, ct))
			{
				return true;
			}
		}

		return false;
	}

	private static async Task<List<object>> CloneEntitiesAsync(
		VibeSwarmDbContext dbContext,
		IEntityType entityType,
		CancellationToken ct)
	{
		var sourceEntities = await LoadEntitiesAsync(dbContext, entityType.ClrType, ct);
		var clones = new List<object>(sourceEntities.Count);

		foreach (var sourceEntity in sourceEntities)
		{
			var clone = Activator.CreateInstance(entityType.ClrType)
				?? throw new InvalidOperationException($"Could not create an instance of {entityType.ClrType.Name}.");

			foreach (var property in entityType.GetProperties())
			{
				if (property.IsShadowProperty() || ShouldSkipPropertyCopy(property))
				{
					continue;
				}

				var propertyInfo = property.PropertyInfo;
				if (propertyInfo == null || !propertyInfo.CanRead || !propertyInfo.CanWrite)
				{
					continue;
				}

				propertyInfo.SetValue(clone, propertyInfo.GetValue(sourceEntity));
			}

			clones.Add(clone);
		}

		return clones;
	}

	private static bool ShouldSkipPropertyCopy(IProperty property)
	{
		if (property.IsConcurrencyToken && property.ValueGenerated == ValueGenerated.OnAddOrUpdate)
		{
			return true;
		}

		if (!property.IsPrimaryKey())
		{
			return false;
		}

		return property.ValueGenerated != ValueGenerated.Never &&
			(property.ClrType == typeof(int) ||
				property.ClrType == typeof(long) ||
				property.ClrType == typeof(int?) ||
				property.ClrType == typeof(long?));
	}

	private static Task<IReadOnlyList<object>> LoadEntitiesAsync(
		VibeSwarmDbContext dbContext,
		Type clrType,
		CancellationToken ct)
	{
		return (Task<IReadOnlyList<object>>)LoadEntitiesMethod
			.MakeGenericMethod(clrType)
			.Invoke(null, [dbContext, ct])!;
	}

	private static async Task<IReadOnlyList<object>> LoadEntitiesAsyncCore<TEntity>(
		VibeSwarmDbContext dbContext,
		CancellationToken ct)
		where TEntity : class
	{
		var entities = await dbContext.Set<TEntity>()
			.AsNoTracking()
			.ToListAsync(ct);
		return entities.Cast<object>().ToList();
	}

	private static Task AddEntitiesAsync(
		VibeSwarmDbContext dbContext,
		Type clrType,
		IReadOnlyList<object> entities,
		CancellationToken ct)
	{
		return (Task)AddEntitiesMethod
			.MakeGenericMethod(clrType)
			.Invoke(null, [dbContext, entities, ct])!;
	}

	private static async Task AddEntitiesAsyncCore<TEntity>(
		VibeSwarmDbContext dbContext,
		IReadOnlyList<object> entities,
		CancellationToken ct)
		where TEntity : class
	{
		dbContext.Set<TEntity>().AddRange(entities.Cast<TEntity>());
		await dbContext.SaveChangesAsync(ct);
	}

	private static Task<bool> HasAnyEntitiesAsync(
		VibeSwarmDbContext dbContext,
		Type clrType,
		CancellationToken ct)
	{
		return (Task<bool>)HasAnyEntitiesMethod
			.MakeGenericMethod(clrType)
			.Invoke(null, [dbContext, ct])!;
	}

	private static Task<bool> HasAnyEntitiesAsyncCore<TEntity>(
		VibeSwarmDbContext dbContext,
		CancellationToken ct)
		where TEntity : class
	{
		return dbContext.Set<TEntity>().AnyAsync(ct);
	}
}
