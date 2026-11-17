using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Web.Controllers;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

/// <summary>
/// How many jobs run at once: in total, and per provider. A project never runs two jobs at once,
/// whatever the limits, so two sessions never share a checkout.
/// </summary>
public sealed class JobConcurrencyTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly DbContextOptions<VibeSwarmDbContext> _dbOptions;
	private readonly Project _alpha = new() { Id = Guid.NewGuid(), Name = "Alpha", WorkingPath = "/tmp/alpha" };
	private readonly Project _beta = new() { Id = Guid.NewGuid(), Name = "Beta", WorkingPath = "/tmp/beta" };
	private readonly Project _gamma = new() { Id = Guid.NewGuid(), Name = "Gamma", WorkingPath = "/tmp/gamma" };
	private readonly Provider _claude = new() { Id = Guid.NewGuid(), Name = "Claude", Type = ProviderType.Claude, IsEnabled = true, IsDefault = true };
	private readonly Provider _copilot = new() { Id = Guid.NewGuid(), Name = "Copilot", Type = ProviderType.Copilot, IsEnabled = true };

	public JobConcurrencyTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();
		_dbOptions = new DbContextOptionsBuilder<VibeSwarmDbContext>().UseSqlite(_connection).Options;

		using var dbContext = CreateDbContext();
		dbContext.Database.EnsureCreated();
		dbContext.Projects.AddRange(_alpha, _beta, _gamma);
		dbContext.Providers.AddRange(_claude, _copilot);
		dbContext.SaveChanges();
	}

	public void Dispose() => _connection.Dispose();

	[Fact]
	public async Task GetPendingJobsAsync_StartsJobsInSeparateProjectsOnOneProvider_UpToItsLimit()
	{
		await using var dbContext = CreateDbContext();
		await SetProviderLimitAsync(dbContext, _claude, 2);
		AddJob(dbContext, _alpha, _claude, "Alpha job", createdMinutesAgo: 30);
		AddJob(dbContext, _beta, _claude, "Beta job", createdMinutesAgo: 20);
		AddJob(dbContext, _gamma, _claude, "Gamma job", createdMinutesAgo: 10);
		await dbContext.SaveChangesAsync();

		var pendingJobs = await CreateJobService(dbContext).GetPendingJobsAsync();

		Assert.Equal(["Alpha job", "Beta job"], pendingJobs.Select(job => job.Title));
	}

	[Fact]
	public async Task GetPendingJobsAsync_KeepsOneJobPerProject_WhateverTheProviderLimit()
	{
		await using var dbContext = CreateDbContext();
		await SetProviderLimitAsync(dbContext, _claude, 3);
		AddJob(dbContext, _alpha, _claude, "Running in Alpha", createdMinutesAgo: 40, status: JobStatus.Processing);
		AddJob(dbContext, _alpha, _claude, "Queued in Alpha", createdMinutesAgo: 30);
		AddJob(dbContext, _beta, _claude, "First in Beta", createdMinutesAgo: 20);
		AddJob(dbContext, _beta, _copilot, "Second in Beta", createdMinutesAgo: 10);
		await dbContext.SaveChangesAsync();

		var pendingJobs = await CreateJobService(dbContext).GetPendingJobsAsync();

		Assert.Equal(["First in Beta"], pendingJobs.Select(job => job.Title));
	}

	[Fact]
	public async Task GetPendingJobsAsync_CountsRunningJobsAgainstTheProviderLimit()
	{
		await using var dbContext = CreateDbContext();
		await SetProviderLimitAsync(dbContext, _claude, 2);
		AddJob(dbContext, _alpha, _claude, "Running in Alpha", createdMinutesAgo: 40, status: JobStatus.Processing);
		AddJob(dbContext, _beta, _claude, "Beta job", createdMinutesAgo: 20);
		AddJob(dbContext, _gamma, _claude, "Gamma job", createdMinutesAgo: 10);
		await dbContext.SaveChangesAsync();

		var pendingJobs = await CreateJobService(dbContext).GetPendingJobsAsync();

		Assert.Equal(["Beta job"], pendingJobs.Select(job => job.Title));
	}

	[Fact]
	public async Task GetPendingJobsAsync_RunsSeparateProvidersSideBySide()
	{
		await using var dbContext = CreateDbContext();
		AddJob(dbContext, _alpha, _claude, "Alpha on Claude", createdMinutesAgo: 30);
		AddJob(dbContext, _beta, _claude, "Beta on Claude", createdMinutesAgo: 20);
		AddJob(dbContext, _gamma, _copilot, "Gamma on Copilot", createdMinutesAgo: 10);
		await dbContext.SaveChangesAsync();

		var pendingJobs = await CreateJobService(dbContext).GetPendingJobsAsync();

		Assert.Equal(["Alpha on Claude", "Gamma on Copilot"], pendingJobs.Select(job => job.Title));
	}

	[Fact]
	public async Task JobQueueManager_GetPendingJobsAsync_HonoursTheProviderLimit()
	{
		await using (var dbContext = CreateDbContext())
		{
			await SetProviderLimitAsync(dbContext, _claude, 2);
			AddJob(dbContext, _alpha, _claude, "Alpha job", createdMinutesAgo: 30);
			AddJob(dbContext, _beta, _claude, "Beta job", createdMinutesAgo: 20);
			AddJob(dbContext, _gamma, _claude, "Gamma job", createdMinutesAgo: 10);
			await dbContext.SaveChangesAsync();
		}

		var services = new ServiceCollection();
		services.AddDbContext<VibeSwarmDbContext>(options => options.UseSqlite(_connection));
		using var serviceProvider = services.BuildServiceProvider();
		var queueManager = new JobQueueManager(serviceProvider.GetRequiredService<IServiceScopeFactory>());

		var pendingJobs = await queueManager.GetPendingJobsAsync(10);

		Assert.Equal(["Alpha job", "Beta job"], pendingJobs.Select(job => job.Title));
	}

	[Fact]
	public void SelectJobsToStart_CountsJobsTheWorkerHasDispatchedAgainstTheirProvider()
	{
		var provider = new Provider { Id = Guid.NewGuid(), Name = "Claude", MaxConcurrentJobs = 2 };
		var pendingJobs = new[] { "Beta job", "Gamma job" }
			.Select(title => new Job { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), ProviderId = provider.Id, Provider = provider, GoalPrompt = title })
			.ToList();

		// The worker's own task for Alpha has not claimed its job yet, so the database doesn't count it.
		var jobsToStart = JobProcessingService.SelectJobsToStart(pendingJobs, [(Guid.NewGuid(), null, provider.Id)], availableSlots: 5);

		Assert.Equal(["Beta job"], jobsToStart.Select(job => job.GoalPrompt));
	}

	[Fact]
	public void SelectJobsToStart_KeepsOneJobPerProviderByDefault()
	{
		var provider = new Provider { Id = Guid.NewGuid(), Name = "Claude" };
		var pendingJobs = new[] { "Alpha job", "Beta job" }
			.Select(title => new Job { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), ProviderId = provider.Id, Provider = provider, GoalPrompt = title })
			.ToList();

		Assert.Equal(["Alpha job"], JobProcessingService.SelectJobsToStart(pendingJobs, [], availableSlots: 5).Select(job => job.GoalPrompt));
		Assert.Empty(JobProcessingService.SelectJobsToStart(pendingJobs, [(Guid.NewGuid(), null, provider.Id)], availableSlots: 5));
	}

	[Fact]
	public async Task GetQueueStatusAsync_StartsSoon_WhileTheProviderHasRoom()
	{
		await using var dbContext = CreateDbContext();
		await SetProviderLimitAsync(dbContext, _claude, 2);
		AddJob(dbContext, _alpha, _claude, "Running in Alpha", createdMinutesAgo: 40, status: JobStatus.Processing);
		var queued = AddJob(dbContext, _beta, _claude, "Queued in Beta", createdMinutesAgo: 10);
		await dbContext.SaveChangesAsync();

		var status = await CreateJobService(dbContext).GetQueueStatusAsync(queued.Id);

		Assert.Equal(JobQueueWaitReason.StartingSoon, status!.Reason);
	}

	[Fact]
	public async Task GetQueueStatusAsync_WaitsForAFreeSlot_WhenTheTotalLimitIsReached()
	{
		await using var dbContext = CreateDbContext();
		await new SettingsService(dbContext).UpdateMaxConcurrentJobsAsync(1);
		AddJob(dbContext, _alpha, _claude, "Running in Alpha", createdMinutesAgo: 40, status: JobStatus.Processing);
		var queued = AddJob(dbContext, _beta, _copilot, "Queued in Beta", createdMinutesAgo: 10);
		await dbContext.SaveChangesAsync();

		var status = await CreateJobService(dbContext).GetQueueStatusAsync(queued.Id);

		Assert.Equal(JobQueueWaitReason.AllSlotsBusy, status!.Reason);
		Assert.Equal(1, status.MaxConcurrentJobs);
	}

	[Fact]
	public async Task SettingsService_LeavesTheJobLimitToItsOwnAdminOnlyUpdate()
	{
		await using var dbContext = CreateDbContext();
		var service = new SettingsService(dbContext);
		var settings = await service.GetSettingsAsync();
		Assert.Equal(JobConcurrency.DefaultTotalJobs, settings.MaxConcurrentJobs);

		await service.UpdateSettingsAsync(new AppSettings { TimeZoneId = "UTC", MaxConcurrentJobs = 12 });
		Assert.Equal(JobConcurrency.DefaultTotalJobs, (await service.GetSettingsAsync()).MaxConcurrentJobs);

		Assert.Equal(3, (await service.UpdateMaxConcurrentJobsAsync(3)).MaxConcurrentJobs);
		Assert.Equal(JobConcurrency.MinJobs, (await service.UpdateMaxConcurrentJobsAsync(0)).MaxConcurrentJobs);
		Assert.Equal(JobConcurrency.MaxJobs, (await service.UpdateMaxConcurrentJobsAsync(500)).MaxConcurrentJobs);
	}

	[Fact]
	public void SettingsController_LetsOnlyAdminsChangeTheJobLimit()
	{
		var authorize = typeof(SettingsController)
			.GetMethod(nameof(SettingsController.UpdateMaxConcurrentJobs))!
			.GetCustomAttribute<AuthorizeAttribute>();

		Assert.Equal(UserRoles.Admin, authorize?.Roles);
	}

	[Fact]
	public async Task ProviderService_ChangesTheProviderJobLimitOnlyWhenOneIsGiven()
	{
		await using var dbContext = CreateDbContext();
		var service = new ProviderService(dbContext);

		var saved = await service.UpdateUsageLimitsAsync(_claude.Id, new ProviderUsageLimits(80, 85, null, 3));
		Assert.Equal(3, saved.MaxConcurrentJobs);

		saved = await service.UpdateUsageLimitsAsync(_claude.Id, new ProviderUsageLimits(70, 85, null));
		Assert.Equal(3, saved.MaxConcurrentJobs);
		Assert.Equal(70, saved.SessionLimitPauseThresholdPercent);

		saved = await service.UpdateUsageLimitsAsync(_claude.Id, new ProviderUsageLimits(70, 85, null, 0));
		Assert.Equal(JobConcurrency.MinJobs, saved.MaxConcurrentJobs);

		var created = await service.CreateAsync(new Provider { Name = "Another Claude", Type = ProviderType.Claude, MaxConcurrentJobs = 9 });
		Assert.Equal(JobConcurrency.DefaultJobsPerProvider, created.MaxConcurrentJobs);
	}

	[Theory]
	[InlineData(false, typeof(ForbidResult), 1)]
	[InlineData(true, typeof(OkObjectResult), 4)]
	public async Task ProvidersController_LetsOnlyAdminsChangeTheProviderJobLimit(bool isAdmin, Type expectedResult, int expectedLimit)
	{
		await using var dbContext = CreateDbContext();
		var controller = new ProvidersController(new ProviderService(dbContext), null!, null!, null!)
		{
			ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = CreateUser(isAdmin) } }
		};

		var result = await controller.UpdateUsageLimits(_claude.Id, new ProviderUsageLimits(null, null, null, 4), CancellationToken.None);

		Assert.IsType(expectedResult, result);
		Assert.Equal(expectedLimit, await dbContext.Providers.Where(p => p.Id == _claude.Id).Select(p => p.MaxConcurrentJobs).SingleAsync());
	}

	[Fact]
	public async Task ProvidersController_LetsAnyoneChangeThresholdsWithoutTheJobLimit()
	{
		await using var dbContext = CreateDbContext();
		var controller = new ProvidersController(new ProviderService(dbContext), null!, null!, null!)
		{
			ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = CreateUser(isAdmin: false) } }
		};

		var result = await controller.UpdateUsageLimits(_claude.Id, new ProviderUsageLimits(75, null, null), CancellationToken.None);

		Assert.IsType<OkObjectResult>(result);
	}

	[Fact]
	public void SessionLimitPolicy_CountsJobsAlreadyRunningOnTheProvider()
	{
		var now = DateTime.UtcNow;
		UsageLimitWindow[] windows =
		[
			new UsageLimitWindow { Scope = UsageLimitWindowScope.Session, CurrentUsage = 70, MaxUsage = 100, ResetTime = now.AddHours(2) }
		];

		Assert.Null(SessionLimitPolicy.Evaluate(windows, 90, expectedJobUsagePercent: 10, now));

		var hold = SessionLimitPolicy.Evaluate(windows, 90, expectedJobUsagePercent: 10, now, otherRunningJobs: 2);

		Assert.NotNull(hold);
		Assert.Equal(2, hold.OtherRunningJobs);
		Assert.Contains("with 2 more running", hold.Describe("Claude"));
	}

	[Fact]
	public void ProjectCheckouts_GroupsProjectsInTheSameOrANestedFolder()
	{
		var app = Guid.NewGuid();
		var sameFolder = Guid.NewGuid();
		var nested = Guid.NewGuid();
		var lookalike = Guid.NewGuid();
		var elsewhere = Guid.NewGuid();

		var checkouts = ProjectCheckouts.From(
		[
			(app, "/tmp/vs-checkouts/app"),
			(sameFolder, "/tmp/vs-checkouts/app/"),
			(nested, "/tmp/vs-checkouts/app/web"),
			(lookalike, "/tmp/vs-checkouts/app-two"),
			(elsewhere, "/tmp/vs-checkouts/other")
		]);

		Assert.Equal(checkouts.CheckoutOf(app), checkouts.CheckoutOf(sameFolder));
		Assert.Equal(checkouts.CheckoutOf(app), checkouts.CheckoutOf(nested));
		Assert.Equal([app, sameFolder, nested], checkouts.ProjectsSharing(nested).OrderBy(id => id == app ? 0 : id == sameFolder ? 1 : 2));
		Assert.Equal([lookalike], checkouts.ProjectsSharing(lookalike));
		Assert.Equal([elsewhere], checkouts.ProjectsSharing(elsewhere));
	}

	[Fact]
	public async Task GetPendingJobsAsync_HoldsBackAProjectWhoseFolderAnotherRunningJobWorksIn()
	{
		await using var dbContext = CreateDbContext();
		var app = new Project { Id = Guid.NewGuid(), Name = "App", WorkingPath = "/tmp/vs-shared/app" };
		var web = new Project { Id = Guid.NewGuid(), Name = "App web", WorkingPath = "/tmp/vs-shared/app/web" };
		dbContext.Projects.AddRange(app, web);
		await new SettingsService(dbContext).UpdateMaxConcurrentJobsAsync(10);
		AddJob(dbContext, app, _claude, "Running in App", createdMinutesAgo: 40, status: JobStatus.Processing);
		var queued = AddJob(dbContext, web, _copilot, "Queued in App web", createdMinutesAgo: 20);
		AddJob(dbContext, _beta, _copilot, "Beta job", createdMinutesAgo: 10);
		await dbContext.SaveChangesAsync();
		var jobService = CreateJobService(dbContext);

		Assert.Equal(["Beta job"], (await jobService.GetPendingJobsAsync()).Select(job => job.Title));

		var status = await jobService.GetQueueStatusAsync(queued.Id);
		Assert.Equal(JobQueueWaitReason.ProjectBusy, status!.Reason);
		Assert.True(status.BlockingProjectSharesFolder);
		Assert.Equal("App", status.BlockingProjectName);
	}

	[Fact]
	public void SelectJobsToStart_StartsOneJobPerSharedCheckout()
	{
		var app = Guid.NewGuid();
		var web = Guid.NewGuid();
		var checkouts = ProjectCheckouts.From([(app, "/tmp/vs-shared/app"), (web, "/tmp/vs-shared/app/web")]);
		var pendingJobs = new[]
		{
			new Job { Id = Guid.NewGuid(), ProjectId = app, GoalPrompt = "App job" },
			new Job { Id = Guid.NewGuid(), ProjectId = web, GoalPrompt = "Web job" }
		};

		Assert.Equal(["App job"], JobProcessingService.SelectJobsToStart(pendingJobs, [], availableSlots: 5, checkouts).Select(job => job.GoalPrompt));
		Assert.Empty(JobProcessingService.SelectJobsToStart(pendingJobs.Skip(1), [(app, null, Guid.Empty)], availableSlots: 5, checkouts));
	}

	private VibeSwarmDbContext CreateDbContext() => new(_dbOptions);

	private static JobService CreateJobService(VibeSwarmDbContext dbContext)
		=> new(dbContext, new ServiceCollection().BuildServiceProvider());

	private static ClaimsPrincipal CreateUser(bool isAdmin)
	{
		var claims = new List<Claim> { new(ClaimTypes.Name, "someone") };
		if (isAdmin)
		{
			claims.Add(new Claim(ClaimTypes.Role, UserRoles.Admin));
		}

		return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
	}

	private static async Task SetProviderLimitAsync(VibeSwarmDbContext dbContext, Provider provider, int limit)
	{
		await new ProviderService(dbContext).UpdateUsageLimitsAsync(provider.Id, new ProviderUsageLimits(null, null, null, limit));
	}

	private static Job AddJob(VibeSwarmDbContext dbContext, Project project, Provider provider, string title,
		int createdMinutesAgo, JobStatus status = JobStatus.New)
	{
		var job = new Job
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			ProviderId = provider.Id,
			Title = title,
			GoalPrompt = title,
			Status = status,
			CreatedAt = DateTime.UtcNow.AddMinutes(-createdMinutesAgo)
		};
		dbContext.Jobs.Add(job);
		return job;
	}
}
