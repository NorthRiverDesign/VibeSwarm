using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Services;

public class DatabaseService : IDatabaseService
{
	private readonly VibeSwarmDbContext _db;

	public DatabaseService(VibeSwarmDbContext db) => _db = db;

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
					UpdatedAt = DateTime.UtcNow,
				});
				await _db.SaveChangesAsync(ct);
				result.Imported.Add("App settings");
			}
		}

		return result;
	}

	private static string TruncateLabel(string text, int maxLength = 50)
		=> text.Length <= maxLength ? text : text[..maxLength] + "…";
}
