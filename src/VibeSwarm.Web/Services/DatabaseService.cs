using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Services;

public class DatabaseService : IDatabaseService
{
	private readonly VibeSwarmDbContext _db;
	private readonly ICriticalErrorLogService _criticalErrorLogService;

	public DatabaseService(VibeSwarmDbContext db, ICriticalErrorLogService criticalErrorLogService)
	{
		_db = db;
		_criticalErrorLogService = criticalErrorLogService;
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

		var teamRoles = await _db.TeamRoles
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
			TeamRoles = teamRoles.Select(r => new TeamRoleExportDto
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
		var existingRoleNames = await _db.TeamRoles.Select(r => r.Name).ToHashSetAsync(ct);

		foreach (var roleDto in export.TeamRoles)
		{
			if (string.IsNullOrWhiteSpace(roleDto.Name))
				continue;

			if (existingRoleNames.Contains(roleDto.Name))
			{
				result.Skipped.Add($"Team role '{roleDto.Name}' already exists");
				continue;
			}

			var role = new TeamRole
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
					role.SkillLinks.Add(new TeamRoleSkill { TeamRoleId = role.Id, SkillId = skill.Id });
				}
				else
				{
					var existingSkill = await _db.Skills.FirstOrDefaultAsync(s => s.Name == skillName, ct);
					if (existingSkill != null)
						role.SkillLinks.Add(new TeamRoleSkill { TeamRoleId = role.Id, SkillId = existingSkill.Id });
				}
			}

			_db.TeamRoles.Add(role);
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
}
