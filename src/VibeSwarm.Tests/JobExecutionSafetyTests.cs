using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.VersionControl;
using VibeSwarm.Shared.VersionControl.Models;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class JobExecutionSafetyTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly DbContextOptions<VibeSwarmDbContext> _dbOptions;

	public JobExecutionSafetyTests()
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
	public void GetCompletionCriteria_UsesTenMinuteDefaultAndProviderOverride()
	{
		var provider = new Provider
		{
			StallTimeoutSeconds = 900
		};

		var defaultJob = new Job
		{
			GoalPrompt = "Default timeout",
			Provider = new Provider()
		};

		var providerOverrideJob = new Job
		{
			GoalPrompt = "Provider timeout",
			Provider = provider
		};

		var jobOverride = new Job
		{
			GoalPrompt = "Job timeout",
			Provider = provider,
			StallTimeoutSeconds = 120
		};

		Assert.Equal(TimeSpan.FromMinutes(10), defaultJob.GetCompletionCriteria().StallTimeout);
		Assert.Equal(TimeSpan.FromMinutes(15), providerOverrideJob.GetCompletionCriteria().StallTimeout);
		Assert.Equal(TimeSpan.FromMinutes(2), jobOverride.GetCompletionCriteria().StallTimeout);
	}

	[Fact]
	public async Task ClaimJobAsync_AllowsOnlyTheFirstClaim()
	{
		var projectId = Guid.NewGuid();
		var providerId = Guid.NewGuid();
		var jobId = Guid.NewGuid();

		await using (var setupContext = CreateDbContext())
		{
			setupContext.Projects.Add(new Project
			{
				Id = projectId,
				Name = "Execution Safety Project",
				WorkingPath = "/tmp/execution-safety-project"
			});
			setupContext.Providers.Add(new Provider
			{
				Id = providerId,
				Name = "Copilot",
				Type = ProviderType.Copilot,
				IsEnabled = true
			});
			setupContext.Jobs.Add(new Job
			{
				Id = jobId,
				ProjectId = projectId,
				ProviderId = providerId,
				GoalPrompt = "Run only once",
				Status = JobStatus.New
			});

			await setupContext.SaveChangesAsync();
		}

		var serviceProvider = new ServiceCollection().BuildServiceProvider();
		var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
		var processingService = new JobProcessingService(
			scopeFactory,
			NullLogger<JobProcessingService>.Instance,
			new NoOpVersionControlService(),
			projectEnvironmentCredentialService: new NoOpProjectEnvironmentCredentialService());

		await using var firstContext = CreateDbContext();
		await using var secondContext = CreateDbContext();

		var firstClaim = await InvokeClaimJobAsync(processingService, jobId, firstContext);
		var secondClaim = await InvokeClaimJobAsync(processingService, jobId, secondContext);

		Assert.True(firstClaim);
		Assert.False(secondClaim);

		await using var verificationContext = CreateDbContext();
		var job = await verificationContext.Jobs.SingleAsync(j => j.Id == jobId);

		Assert.Equal(JobStatus.Started, job.Status);
		Assert.False(string.IsNullOrWhiteSpace(job.WorkerInstanceId));
		Assert.NotNull(job.StartedAt);
		Assert.NotNull(job.LastHeartbeatAt);
	}

	private VibeSwarmDbContext CreateDbContext() => new(_dbOptions);

	private static async Task<bool> InvokeClaimJobAsync(JobProcessingService service, Guid jobId, VibeSwarmDbContext dbContext)
	{
		var method = typeof(JobProcessingService).GetMethod("ClaimJobAsync", BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);

		var task = (Task<bool>)method.Invoke(service, [jobId, dbContext, CancellationToken.None])!;
		return await task;
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
	}

	private sealed class NoOpVersionControlService : IVersionControlService
	{
		public Task<bool> IsGitAvailableAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> IsGitRepositoryAsync(string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<string?> GetCurrentCommitHashAsync(string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<string?> GetCurrentBranchAsync(string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<string?> GetRemoteUrlAsync(string workingDirectory, string remoteName = "origin", CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> HasUncommittedChangesAsync(string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitWorkingTreeStatus> GetWorkingTreeStatusAsync(string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IReadOnlyList<string>> GetChangedFilesAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<string?> GetWorkingDirectoryDiffAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<string?> GetCommitRangeDiffAsync(string workingDirectory, string fromCommit, string? toCommit = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitDiffSummary?> GetDiffSummaryAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> CommitAllChangesAsync(string workingDirectory, string commitMessage, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> PushAsync(string workingDirectory, string remoteName = "origin", string? branchName = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> CommitAndPushAsync(string workingDirectory, string commitMessage, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> CreatePullRequestAsync(string workingDirectory, string sourceBranch, string targetBranch, string title, string? body = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> PreviewMergeBranchAsync(string workingDirectory, string sourceBranch, string targetBranch, string remoteName = "origin", CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> MergeBranchAsync(string workingDirectory, string sourceBranch, string targetBranch, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken cancellationToken = default, bool pushAfterMerge = true) => throw new NotSupportedException();
		public Task<IReadOnlyList<GitBranchInfo>> GetBranchesAsync(string workingDirectory, bool includeRemote = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> FetchAsync(string workingDirectory, string remoteName = "origin", bool prune = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> HardCheckoutBranchAsync(string workingDirectory, string branchName, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> SyncWithOriginAsync(string workingDirectory, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> CloneRepositoryAsync(string repositoryUrl, string targetDirectory, string? branch = null, Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public string GetGitHubCloneUrl(string ownerAndRepo, bool useSsh = true) => throw new NotSupportedException();
		public string? ExtractGitHubRepository(string? remoteUrl) => throw new NotSupportedException();
		public Task<GitOperationResult> CreateBranchAsync(string workingDirectory, string branchName, bool switchToBranch = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> DiscardAllChangesAsync(string workingDirectory, bool includeUntracked = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> PreserveChangesAsync(string workingDirectory, string message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IReadOnlyList<string>> GetCommitLogAsync(string workingDirectory, string fromCommit, string? toCommit = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> InitializeRepositoryAsync(string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> IsGitHubCliAvailableAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> IsGitHubCliAuthenticatedAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> CreateGitHubRepositoryAsync(string workingDirectory, string repositoryName, string? description = null, bool isPrivate = false, Action<string>? progressCallback = null, CancellationToken cancellationToken = default, string? gitignoreTemplate = null, string? licenseTemplate = null, bool initializeReadme = false) => throw new NotSupportedException();
		public Task<GitOperationResult> AddRemoteAsync(string workingDirectory, string remoteName, string remoteUrl, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IReadOnlyDictionary<string, string>> GetRemotesAsync(string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> CloneWithGitHubCliAsync(string ownerRepo, string targetDirectory, Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> PruneRemoteBranchesAsync(string workingDirectory, string remoteName = "origin", CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}
}
