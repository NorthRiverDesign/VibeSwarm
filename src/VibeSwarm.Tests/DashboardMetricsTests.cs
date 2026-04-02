using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.VersionControl;
using VibeSwarm.Shared.VersionControl.Models;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class DashboardMetricsTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly DbContextOptions<VibeSwarmDbContext> _dbOptions;

	public DashboardMetricsTests()
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
	public async Task GetDashboardJobMetricsAsync_LastSevenDays_AggregatesCompletedJobsAndDuration()
	{
		await using var dbContext = CreateDbContext();
		var service = CreateProjectService(dbContext);

		var providerId = Guid.NewGuid();
		var projectId = Guid.NewGuid();
		var nowUtc = DateTime.UtcNow;

		dbContext.Providers.Add(new Provider
		{
			Id = providerId,
			Name = "Copilot",
			Type = ProviderType.Copilot,
			ConnectionMode = ProviderConnectionMode.CLI
		});

		dbContext.Projects.Add(new Project
		{
			Id = projectId,
			Name = "Analytics Project",
			WorkingPath = "/tmp/analytics-project"
		});

		dbContext.Jobs.AddRange(
			new Job
			{
				Id = Guid.NewGuid(),
				ProjectId = projectId,
				ProviderId = providerId,
				GoalPrompt = "Recent successful job",
				Status = JobStatus.Completed,
				CompletedAt = nowUtc.AddHours(-6),
				ExecutionDurationSeconds = 120
			},
			new Job
			{
				Id = Guid.NewGuid(),
				ProjectId = projectId,
				ProviderId = providerId,
				GoalPrompt = "Recent completed job with fallback duration",
				Status = JobStatus.Completed,
				StartedAt = nowUtc.AddDays(-2).AddMinutes(-2),
				CompletedAt = nowUtc.AddDays(-2),
			},
			new Job
			{
				Id = Guid.NewGuid(),
				ProjectId = projectId,
				ProviderId = providerId,
				GoalPrompt = "Failed job should not be included",
				Status = JobStatus.Failed,
				CompletedAt = nowUtc.AddDays(-1),
				ExecutionDurationSeconds = 999
			},
			new Job
			{
				Id = Guid.NewGuid(),
				ProjectId = projectId,
				ProviderId = providerId,
				GoalPrompt = "Completed outside selected range",
				Status = JobStatus.Completed,
				CompletedAt = nowUtc.AddDays(-12),
				ExecutionDurationSeconds = 300
			});

		await dbContext.SaveChangesAsync();

		var metrics = await service.GetDashboardJobMetricsAsync(7);

		Assert.Equal(7, metrics.RangeDays);
		Assert.Equal(7, metrics.Buckets.Count);
		Assert.Equal(2, metrics.TotalCompletedJobs);
		Assert.NotNull(metrics.AverageDurationSeconds);
		Assert.InRange(metrics.AverageDurationSeconds!.Value, 119, 121);
		Assert.Equal(2, metrics.Buckets.Sum(bucket => bucket.CompletedJobs));
		Assert.Contains(metrics.Buckets, bucket => bucket.AverageDurationSeconds.HasValue);
	}

	[Fact]
	public async Task GetDashboardJobMetricsAsync_NormalizesInvalidRange_AndUsesExpectedBucketLayouts()
	{
		await using var dbContext = CreateDbContext();
		var service = CreateProjectService(dbContext);
		var providerId = Guid.NewGuid();
		var projectId = Guid.NewGuid();
		var nowUtc = DateTime.UtcNow;

		dbContext.Providers.Add(new Provider
		{
			Id = providerId,
			Name = "Claude",
			Type = ProviderType.Claude,
			ConnectionMode = ProviderConnectionMode.CLI
		});

		dbContext.Projects.Add(new Project
		{
			Id = projectId,
			Name = "Bucket Project",
			WorkingPath = "/tmp/bucket-project"
		});

		dbContext.Jobs.Add(new Job
		{
			Id = Guid.NewGuid(),
			ProjectId = projectId,
			ProviderId = providerId,
			GoalPrompt = "Hourly job",
			Status = JobStatus.Completed,
			CompletedAt = nowUtc.AddHours(-1),
			ExecutionDurationSeconds = 45
		});

		await dbContext.SaveChangesAsync();

		var normalizedMetrics = await service.GetDashboardJobMetricsAsync(42);
		var oneDayMetrics = await service.GetDashboardJobMetricsAsync(1);
		var ninetyDayMetrics = await service.GetDashboardJobMetricsAsync(90);

		Assert.Equal(7, normalizedMetrics.RangeDays);
		Assert.Equal(7, normalizedMetrics.Buckets.Count);
		Assert.Equal(1, oneDayMetrics.RangeDays);
		Assert.Equal(24, oneDayMetrics.Buckets.Count);
		Assert.Equal(90, ninetyDayMetrics.RangeDays);
		Assert.Equal(13, ninetyDayMetrics.Buckets.Count);
	}

	[Fact]
	public async Task GetRecentWithLatestJobAsync_SortsActiveProjectsByLatestRun_AndFallsBackToName()
	{
		await using var dbContext = CreateDbContext();
		var service = CreateProjectService(dbContext);
		var providerId = Guid.NewGuid();
		var nowUtc = DateTime.UtcNow;

		dbContext.Providers.Add(new Provider
		{
			Id = providerId,
			Name = "Copilot",
			Type = ProviderType.Copilot,
			ConnectionMode = ProviderConnectionMode.CLI
		});

		var alphaProject = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Alpha",
			WorkingPath = "/tmp/alpha",
			IsActive = true
		};
		var bravoProject = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Bravo",
			WorkingPath = "/tmp/bravo",
			IsActive = true
		};
		var charlieProject = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Charlie",
			WorkingPath = "/tmp/charlie",
			IsActive = true
		};
		var hiddenProject = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Hidden",
			WorkingPath = "/tmp/hidden",
			IsActive = false
		};

		dbContext.Projects.AddRange(alphaProject, bravoProject, charlieProject, hiddenProject);
		dbContext.Jobs.AddRange(
			new Job
			{
				Id = Guid.NewGuid(),
				ProjectId = alphaProject.Id,
				ProviderId = providerId,
				GoalPrompt = "Older job",
				Status = JobStatus.Completed,
				CreatedAt = nowUtc.AddHours(-8)
			},
			new Job
			{
				Id = Guid.NewGuid(),
				ProjectId = bravoProject.Id,
				ProviderId = providerId,
				GoalPrompt = "Newest job",
				Status = JobStatus.Completed,
				CreatedAt = nowUtc.AddHours(-1)
			},
			new Job
			{
				Id = Guid.NewGuid(),
				ProjectId = hiddenProject.Id,
				ProviderId = providerId,
				GoalPrompt = "Hidden job",
				Status = JobStatus.Completed,
				CreatedAt = nowUtc
			});

		await dbContext.SaveChangesAsync();

		var dashboardProjects = (await service.GetRecentWithLatestJobAsync(10)).ToList();

		Assert.Equal(["Bravo", "Alpha", "Charlie"], dashboardProjects.Select(project => project.Project.Name));
		Assert.All(dashboardProjects, project => Assert.True(project.Project.IsActive));
	}

	[Fact]
	public async Task GetDashboardRunningJobsAsync_ReturnsOneRunningJobPerActiveProject()
	{
		await using var dbContext = CreateDbContext();
		var service = CreateProjectService(dbContext);
		var providerId = Guid.NewGuid();
		var nowUtc = DateTime.UtcNow;

		dbContext.Providers.Add(new Provider
		{
			Id = providerId,
			Name = "Claude",
			Type = ProviderType.Claude,
			ConnectionMode = ProviderConnectionMode.CLI
		});

		var alphaProject = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Alpha",
			WorkingPath = "/tmp/alpha",
			IsActive = true
		};
		var betaProject = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Beta",
			WorkingPath = "/tmp/beta",
			IsActive = true
		};
		var gammaProject = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Gamma",
			WorkingPath = "/tmp/gamma",
			IsActive = true
		};
		var hiddenProject = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Hidden",
			WorkingPath = "/tmp/hidden",
			IsActive = false
		};

		dbContext.Projects.AddRange(alphaProject, betaProject, gammaProject, hiddenProject);
		dbContext.Jobs.AddRange(
			new Job
			{
				Id = Guid.NewGuid(),
				ProjectId = alphaProject.Id,
				ProviderId = providerId,
				GoalPrompt = "Older alpha job",
				Status = JobStatus.Processing,
				CreatedAt = nowUtc.AddMinutes(-50),
				StartedAt = nowUtc.AddMinutes(-45)
			},
			new Job
			{
				Id = Guid.NewGuid(),
				ProjectId = alphaProject.Id,
				ProviderId = providerId,
				Title = "Newest alpha job",
				GoalPrompt = "Newest alpha job",
				Status = JobStatus.Started,
				CreatedAt = nowUtc.AddMinutes(-12),
				StartedAt = nowUtc.AddMinutes(-10),
				CurrentActivity = "Applying changes"
			},
			new Job
			{
				Id = Guid.NewGuid(),
				ProjectId = betaProject.Id,
				ProviderId = providerId,
				Title = "Beta planning job",
				GoalPrompt = "Beta planning job",
				Status = JobStatus.Planning,
				CreatedAt = nowUtc.AddMinutes(-8),
				StartedAt = nowUtc.AddMinutes(-7)
			},
			new Job
			{
				Id = Guid.NewGuid(),
				ProjectId = gammaProject.Id,
				ProviderId = providerId,
				GoalPrompt = "Queued gamma job",
				Status = JobStatus.New,
				CreatedAt = nowUtc.AddMinutes(-5)
			},
			new Job
			{
				Id = Guid.NewGuid(),
				ProjectId = hiddenProject.Id,
				ProviderId = providerId,
				GoalPrompt = "Hidden running job",
				Status = JobStatus.Processing,
				CreatedAt = nowUtc.AddMinutes(-3),
				StartedAt = nowUtc.AddMinutes(-2)
			});

		await dbContext.SaveChangesAsync();

		var runningJobs = (await service.GetDashboardRunningJobsAsync()).ToList();

		Assert.Equal(["Beta", "Alpha"], runningJobs.Select(item => item.Project.Name));
		Assert.Equal("Beta planning job", runningJobs[0].Job.DisplayTitle);
		Assert.Equal("Newest alpha job", runningJobs[1].Job.DisplayTitle);
		Assert.All(runningJobs, item => Assert.True(item.Job.Status is JobStatus.Started or JobStatus.Planning or JobStatus.Processing));
	}

	private VibeSwarmDbContext CreateDbContext() => new(_dbOptions);

	private static ProjectService CreateProjectService(VibeSwarmDbContext dbContext)
	{
		return new ProjectService(dbContext, new NoOpProjectEnvironmentCredentialService(), new NoOpVersionControlService());
	}

	public void Dispose()
	{
		_connection.Dispose();
	}

	private sealed class NoOpProjectEnvironmentCredentialService : IProjectEnvironmentCredentialService
	{
		public void PrepareForStorage(Project project, IReadOnlyCollection<ProjectEnvironment>? existingEnvironments = null) { }
		public void PopulateForEditing(Project? project) { }
		public void PopulateForExecution(Project? project) { }
		public Dictionary<string, string>? BuildJobEnvironmentVariables(Project? project) => null;
	}

	private sealed class NoOpVersionControlService : IVersionControlService
	{
		public Task<bool> IsGitAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
		public Task<bool> IsGitRepositoryAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult(false);
		public Task<string?> GetCurrentCommitHashAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
		public Task<string?> GetCurrentBranchAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
		public Task<string?> GetRemoteUrlAsync(string workingDirectory, string remoteName = "origin", CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
		public Task<bool> HasUncommittedChangesAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult(false);
		public Task<IReadOnlyList<string>> GetChangedFilesAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
		public Task<string?> GetWorkingDirectoryDiffAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
		public Task<string?> GetCommitRangeDiffAsync(string workingDirectory, string fromCommit, string? toCommit = null, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
		public Task<GitDiffSummary?> GetDiffSummaryAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default) => Task.FromResult<GitDiffSummary?>(null);
		public Task<GitOperationResult> CommitAllChangesAsync(string workingDirectory, string commitMessage, CancellationToken cancellationToken = default, GitCommitOptions? commitOptions = null) => throw new NotSupportedException();
		public Task<GitOperationResult> PushAsync(string workingDirectory, string remoteName = "origin", string? branchName = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> CommitAndPushAsync(string workingDirectory, string commitMessage, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> CreatePullRequestAsync(string workingDirectory, string sourceBranch, string targetBranch, string title, string? body = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> PreviewMergeBranchAsync(string workingDirectory, string sourceBranch, string targetBranch, string remoteName = "origin", CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> MergeBranchAsync(string workingDirectory, string sourceBranch, string targetBranch, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken cancellationToken = default, bool pushAfterMerge = true, IReadOnlyList<MergeConflictResolution>? conflictResolutions = null) => throw new NotSupportedException();
		public Task<IReadOnlyList<GitBranchInfo>> GetBranchesAsync(string workingDirectory, bool includeRemote = true, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GitBranchInfo>>([]);
		public Task<GitOperationResult> FetchAsync(string workingDirectory, string remoteName = "origin", bool prune = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> HardCheckoutBranchAsync(string workingDirectory, string branchName, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> SyncWithOriginAsync(string workingDirectory, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> CloneRepositoryAsync(string repositoryUrl, string targetDirectory, string? branch = null, Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public string GetGitHubCloneUrl(string ownerAndRepo, bool useSsh = true) => useSsh ? $"git@github.com:{ownerAndRepo}.git" : $"https://github.com/{ownerAndRepo}.git";
		public string? ExtractGitHubRepository(string? remoteUrl) => null;
		public Task<GitOperationResult> CreateBranchAsync(string workingDirectory, string branchName, bool switchToBranch = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitWorkingTreeStatus> GetWorkingTreeStatusAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult(new GitWorkingTreeStatus());
		public Task<GitOperationResult> PreserveChangesAsync(string workingDirectory, string message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> DiscardAllChangesAsync(string workingDirectory, bool includeUntracked = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IReadOnlyList<string>> GetCommitLogAsync(string workingDirectory, string fromCommit, string? toCommit = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> InitializeRepositoryAsync(string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> IsGitHubCliAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
		public Task<bool> IsGitHubCliAuthenticatedAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
		public Task<GitOperationResult> CreateGitHubRepositoryAsync(string workingDirectory, string repositoryName, string? description = null, bool isPrivate = false, Action<string>? progressCallback = null, CancellationToken cancellationToken = default, string? gitignoreTemplate = null, string? licenseTemplate = null, bool initializeReadme = false) => throw new NotSupportedException();
		public Task<GitOperationResult> AddRemoteAsync(string workingDirectory, string remoteName, string remoteUrl, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IReadOnlyDictionary<string, string>> GetRemotesAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
		public Task<GitOperationResult> CloneWithGitHubCliAsync(string ownerRepo, string targetDirectory, Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> PruneRemoteBranchesAsync(string workingDirectory, string remoteName = "origin", CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}
}
