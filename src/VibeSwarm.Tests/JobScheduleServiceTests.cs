using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.Utilities;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class JobScheduleServiceTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly DbContextOptions<VibeSwarmDbContext> _dbOptions;

	public JobScheduleServiceTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();

		_dbOptions = new DbContextOptionsBuilder<VibeSwarmDbContext>()
			.UseSqlite(_connection)
			.Options;

		using var dbContext = CreateDbContext();
		dbContext.Database.EnsureCreated();
	}

	[Fact]
	public async Task CreateAsync_ComputesNextRunForDailySchedule()
	{
		await using var dbContext = CreateDbContext();
		var project = CreateProject();
		var provider = CreateProvider();
		dbContext.Projects.Add(project);
		dbContext.Providers.Add(provider);
		await dbContext.SaveChangesAsync();

		var service = new JobScheduleService(dbContext);
		var beforeCreate = DateTime.UtcNow;

		var created = await service.CreateAsync(new JobSchedule
		{
			ProjectId = project.Id,
			ProviderId = provider.Id,
			Prompt = "update dependencies",
			Frequency = JobScheduleFrequency.Daily,
			HourUtc = 9,
			MinuteUtc = 30
		});

		var expected = JobScheduleCalculator.CalculateNextRunUtc(created, beforeCreate);
		Assert.Equal(expected, created.NextRunAtUtc);
		Assert.True(created.IsEnabled);
	}

	[Fact]
	public async Task SetEnabledAsync_RecomputesFutureRunWhenResumingPausedSchedule()
	{
		await using var dbContext = CreateDbContext();
		var project = CreateProject();
		var provider = CreateProvider();
		dbContext.Projects.Add(project);
		dbContext.Providers.Add(provider);

		var schedule = new JobSchedule
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			ProviderId = provider.Id,
			Prompt = "run maintenance",
			Frequency = JobScheduleFrequency.Hourly,
			MinuteUtc = 15,
			IsEnabled = false,
			NextRunAtUtc = DateTime.UtcNow.AddHours(-4)
		};
		dbContext.JobSchedules.Add(schedule);
		await dbContext.SaveChangesAsync();

		var service = new JobScheduleService(dbContext);
		var resumed = await service.SetEnabledAsync(schedule.Id, true);

		Assert.True(resumed.IsEnabled);
		Assert.True(resumed.NextRunAtUtc > DateTime.UtcNow.AddMinutes(-1));
		Assert.Null(resumed.LastError);
	}

	[Fact]
	public async Task CreateAsync_UsesConfiguredTimezoneForDailySchedule()
	{
		await using var dbContext = CreateDbContext();
		var project = CreateProject();
		var provider = CreateProvider();
		var timeZoneId = DateTimeHelper.ResolveTimeZone("America/New_York").Id;
		dbContext.Projects.Add(project);
		dbContext.Providers.Add(provider);
		dbContext.AppSettings.Add(new AppSettings
		{
			Id = Guid.NewGuid(),
			TimeZoneId = timeZoneId,
			UpdatedAt = DateTime.UtcNow
		});
		await dbContext.SaveChangesAsync();

		var service = new JobScheduleService(dbContext);
		var beforeCreate = DateTime.UtcNow;
		var timeZone = DateTimeHelper.ResolveTimeZone(timeZoneId);

		var created = await service.CreateAsync(new JobSchedule
		{
			ProjectId = project.Id,
			ProviderId = provider.Id,
			Prompt = "run daily review",
			Frequency = JobScheduleFrequency.Daily,
			HourUtc = 9,
			MinuteUtc = 15
		});

		var expected = JobScheduleCalculator.CalculateNextRunUtc(created, beforeCreate, timeZone);
		Assert.Equal(expected, created.NextRunAtUtc);
	}

	[Fact]
	public async Task ProcessDueSchedulesAsync_CreatesOnlyOneJobPerScheduledRun()
	{
		await using var dbContext = CreateDbContext();
		var project = CreateProject();
		var provider = CreateProvider();
		dbContext.Projects.Add(project);
		dbContext.Providers.Add(provider);

		var dueTime = DateTime.UtcNow.AddMinutes(-2);
		var schedule = new JobSchedule
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			ProviderId = provider.Id,
			Prompt = "check dependencies",
			Frequency = JobScheduleFrequency.Hourly,
			MinuteUtc = dueTime.Minute,
			NextRunAtUtc = dueTime,
			IsEnabled = true
		};
		dbContext.JobSchedules.Add(schedule);
		await dbContext.SaveChangesAsync();

		var serviceProvider = new ServiceCollection().BuildServiceProvider();
		var jobService = new JobService(dbContext, serviceProvider);
		var processor = new JobScheduleProcessor(dbContext, jobService, NullLogger<JobScheduleProcessor>.Instance);

		var firstCount = await processor.ProcessDueSchedulesAsync();
		var secondCount = await processor.ProcessDueSchedulesAsync();

		Assert.Equal(1, firstCount);
		Assert.Equal(0, secondCount);
		Assert.Equal(1, await dbContext.Jobs.CountAsync(job => job.JobScheduleId == schedule.Id));
		var savedJob = await dbContext.Jobs.SingleAsync(job => job.JobScheduleId == schedule.Id);
		Assert.True(savedJob.IsScheduled);
		Assert.Equal(dueTime, savedJob.ScheduledForUtc);
	}

	[Fact]
	public async Task CreateAsync_RejectsTeamRoleThatIsNotAssignedToProject()
	{
		await using var dbContext = CreateDbContext();
		var project = CreateProject();
		var provider = CreateProvider();
		var teamRole = CreateTeamRole();
		dbContext.Projects.Add(project);
		dbContext.Providers.Add(provider);
		dbContext.TeamRoles.Add(teamRole);
		await dbContext.SaveChangesAsync();

		var service = new JobScheduleService(dbContext);

		var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(new JobSchedule
		{
			ProjectId = project.Id,
			ExecutionTarget = JobScheduleExecutionTarget.TeamRole,
			TeamRoleId = teamRole.Id,
			Prompt = "review security findings",
			Frequency = JobScheduleFrequency.Daily,
			HourUtc = 9,
			MinuteUtc = 0
		}));

		Assert.Equal("The selected team member is not assigned to this project.", exception.Message);
	}

	[Fact]
	public async Task ProcessDueSchedulesAsync_CreatesTeamRoleJobFromProjectAssignmentWithoutSwarmFanout()
	{
		await using var dbContext = CreateDbContext();
		var project = CreateProject();
		project.EnableTeamSwarm = true;
		var primaryProvider = CreateProvider();
		var secondaryProvider = CreateProvider();
		var reviewerRole = CreateTeamRole("Security Reviewer");
		var backupRole = CreateTeamRole("Dependency Reviewer");
		dbContext.Projects.Add(project);
		dbContext.Providers.AddRange(primaryProvider, secondaryProvider);
		dbContext.TeamRoles.AddRange(reviewerRole, backupRole);
		dbContext.ProjectTeamRoles.AddRange(
			new ProjectTeamRole
			{
				ProjectId = project.Id,
				TeamRoleId = reviewerRole.Id,
				ProviderId = primaryProvider.Id,
				PreferredModelId = "copilot-reviewer",
				PreferredReasoningEffort = "high",
				IsEnabled = true
			},
			new ProjectTeamRole
			{
				ProjectId = project.Id,
				TeamRoleId = backupRole.Id,
				ProviderId = secondaryProvider.Id,
				PreferredModelId = "copilot-backup",
				PreferredReasoningEffort = "medium",
				IsEnabled = true
			});

		dbContext.ProviderModels.AddRange(
			new ProviderModel
			{
				Id = Guid.NewGuid(),
				ProviderId = primaryProvider.Id,
				ModelId = "copilot-reviewer",
				DisplayName = "Copilot Reviewer",
				IsAvailable = true,
				IsDefault = true,
				UpdatedAt = DateTime.UtcNow
			},
			new ProviderModel
			{
				Id = Guid.NewGuid(),
				ProviderId = secondaryProvider.Id,
				ModelId = "copilot-backup",
				DisplayName = "Copilot Backup",
				IsAvailable = true,
				IsDefault = true,
				UpdatedAt = DateTime.UtcNow
			});

		var dueTime = DateTime.UtcNow.AddMinutes(-2);
		dbContext.JobSchedules.Add(new JobSchedule
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			ExecutionTarget = JobScheduleExecutionTarget.TeamRole,
			TeamRoleId = reviewerRole.Id,
			Prompt = "review for security issues",
			Frequency = JobScheduleFrequency.Hourly,
			MinuteUtc = dueTime.Minute,
			NextRunAtUtc = dueTime,
			IsEnabled = true
		});
		await dbContext.SaveChangesAsync();

		var serviceProvider = new ServiceCollection().BuildServiceProvider();
		var jobService = new JobService(dbContext, serviceProvider);
		var processor = new JobScheduleProcessor(dbContext, jobService, NullLogger<JobScheduleProcessor>.Instance);

		var createdCount = await processor.ProcessDueSchedulesAsync();

		Assert.Equal(1, createdCount);
		var jobs = await dbContext.Jobs.ToListAsync();
		Assert.Single(jobs);
		var job = jobs[0];
		Assert.True(job.IsScheduled);
		Assert.Equal(reviewerRole.Id, job.TeamRoleId);
		Assert.Equal(primaryProvider.Id, job.ProviderId);
		Assert.Equal("copilot-reviewer", job.ModelUsed);
		Assert.Equal("high", job.ReasoningEffort);
		Assert.Null(job.SwarmId);
	}

	private VibeSwarmDbContext CreateDbContext() => new(_dbOptions);

	private static Project CreateProject() => new()
	{
		Id = Guid.NewGuid(),
		Name = $"Project-{Guid.NewGuid():N}",
		WorkingPath = "/tmp/scheduler-project"
	};

	private static Provider CreateProvider() => new()
	{
		Id = Guid.NewGuid(),
		Name = $"Provider-{Guid.NewGuid():N}",
		Type = ProviderType.Copilot,
		IsEnabled = true,
		IsDefault = true
	};

	private static TeamRole CreateTeamRole(string? name = null) => new()
	{
		Id = Guid.NewGuid(),
		Name = name ?? $"Role-{Guid.NewGuid():N}",
		IsEnabled = true
	};

	public void Dispose()
	{
		_connection.Dispose();
	}
}
