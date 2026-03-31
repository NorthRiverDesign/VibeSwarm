using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.VersionControl;
using VibeSwarm.Shared.VersionControl.Models;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class JobCompletionMonitorServiceTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly DbContextOptions<VibeSwarmDbContext> _dbOptions;

	public JobCompletionMonitorServiceTests()
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
	public async Task CheckRunningJobsAsync_CompletesProcessingJobWhenSessionOutputAlreadyFinished()
	{
		var projectId = Guid.NewGuid();
		var providerId = Guid.NewGuid();
		var jobId = Guid.NewGuid();
		var now = DateTime.UtcNow;

		await using (var setupContext = CreateDbContext())
		{
			setupContext.Projects.Add(new Project
			{
				Id = projectId,
				Name = "Follow Up Recovery Project",
				WorkingPath = "/tmp/follow-up-recovery"
			});

			setupContext.Providers.Add(new Provider
			{
				Id = providerId,
				Name = "Recovery Provider",
				Type = ProviderType.Copilot,
				IsEnabled = true
			});

			await setupContext.SaveChangesAsync();

			setupContext.Jobs.Add(new Job
			{
				Id = jobId,
				ProjectId = projectId,
				ProviderId = providerId,
				GoalPrompt = "Follow up on the previous change",
				Status = JobStatus.Processing,
				StartedAt = now.AddMinutes(-2),
				LastActivityAt = now.AddMinutes(-1),
				LastHeartbeatAt = now.AddSeconds(-30),
				CurrentActivity = "Waiting for CLI response...",
				ConsoleOutput = """
					[Assistant] Reviewing the repository state
					[Tool] bash: git status --short
					[Session] Complete
					"""
			});

			await setupContext.SaveChangesAsync();
		}

		var services = BuildServices();
		var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
		var versionControlService = services.GetRequiredService<IVersionControlService>();
		var processingService = new JobProcessingService(
			scopeFactory,
			NullLogger<JobProcessingService>.Instance,
			versionControlService,
			projectEnvironmentCredentialService: new NoOpProjectEnvironmentCredentialService());

		var monitor = new JobCompletionMonitorService(
			scopeFactory,
			NullLogger<JobCompletionMonitorService>.Instance,
			new ProviderHealthTracker(),
			processingService,
			versionControlService,
			new ProcessSupervisor());

		await InvokeCheckRunningJobsAsync(monitor);

		await using var verificationContext = CreateDbContext();
		var job = await verificationContext.Jobs.SingleAsync(j => j.Id == jobId);

		Assert.Equal(JobStatus.Completed, job.Status);
		Assert.NotNull(job.CompletedAt);
		Assert.Null(job.CurrentActivity);
		Assert.Equal(0, job.ChangedFilesCount);
	}

	/// <summary>
	/// An agent reading source code that contains "[Session] Complete" as part of a longer line
	/// (e.g., OutputLine = "[Session] Complete") must NOT trigger the recovery path.
	/// </summary>
	[Fact]
	public async Task CheckRunningJobsAsync_DoesNotCompleteJobWhenMarkerIsSubstringOfLongerLine()
	{
		var projectId = Guid.NewGuid();
		var providerId = Guid.NewGuid();
		var jobId = Guid.NewGuid();
		var now = DateTime.UtcNow;

		await using (var setupContext = CreateDbContext())
		{
			setupContext.Projects.Add(new Project
			{
				Id = projectId,
				Name = "Refactoring Project",
				WorkingPath = "/tmp/refactoring"
			});

			setupContext.Providers.Add(new Provider
			{
				Id = providerId,
				Name = "Claude Provider",
				Type = ProviderType.Claude,
				IsEnabled = true
			});

			await setupContext.SaveChangesAsync();

			// Simulates an agent that read source code containing the marker as a substring:
			// OutputLine = "[Session] Complete"   ← not a standalone marker line
			setupContext.Jobs.Add(new Job
			{
				Id = jobId,
				ProjectId = projectId,
				ProviderId = providerId,
				GoalPrompt = "Major refactoring of the authentication module",
				Status = JobStatus.Processing,
				StartedAt = now.AddMinutes(-2),
				LastActivityAt = now.AddMinutes(-1),
				LastHeartbeatAt = now.AddSeconds(-30),
				CurrentActivity = "Starting major refactoring...",
				ConsoleOutput = """
					[Assistant] I will start a major refactoring.
					[Tool] read_file: CopilotSdkProvider.cs
					OutputLine = "[Session] Complete",
					[Assistant] I have read the file and will now proceed.
					"""
			});

			await setupContext.SaveChangesAsync();
		}

		var services = BuildServices();
		var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
		var versionControlService = services.GetRequiredService<IVersionControlService>();
		var processingService = new JobProcessingService(
			scopeFactory,
			NullLogger<JobProcessingService>.Instance,
			versionControlService,
			projectEnvironmentCredentialService: new NoOpProjectEnvironmentCredentialService());

		var monitor = new JobCompletionMonitorService(
			scopeFactory,
			NullLogger<JobCompletionMonitorService>.Instance,
			new ProviderHealthTracker(),
			processingService,
			versionControlService,
			new ProcessSupervisor());

		await InvokeCheckRunningJobsAsync(monitor);

		await using var verificationContext = CreateDbContext();
		var job = await verificationContext.Jobs.SingleAsync(j => j.Id == jobId);

		// Job should remain Processing — the marker was a substring inside a code line, not a standalone marker
		Assert.Equal(JobStatus.Processing, job.Status);
		Assert.Null(job.CompletedAt);
	}

	private ServiceProvider BuildServices()
	{
		var services = new ServiceCollection();
		services.AddDbContext<VibeSwarmDbContext>(options => options.UseSqlite(_connection));
		services.AddSingleton<IVersionControlService, NoOpVersionControlService>();
		return services.BuildServiceProvider();
	}

	private VibeSwarmDbContext CreateDbContext() => new(_dbOptions);

	private static async Task InvokeCheckRunningJobsAsync(JobCompletionMonitorService service)
	{
		var method = typeof(JobCompletionMonitorService).GetMethod("CheckRunningJobsAsync", BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);

		var task = (Task)method.Invoke(service, [CancellationToken.None])!;
		await task;
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
		public Task<bool> IsGitAvailableAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> IsGitRepositoryAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult(false);
		public Task<string?> GetCurrentCommitHashAsync(string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<string?> GetCurrentBranchAsync(string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<string?> GetRemoteUrlAsync(string workingDirectory, string remoteName = "origin", CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> HasUncommittedChangesAsync(string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitWorkingTreeStatus> GetWorkingTreeStatusAsync(string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IReadOnlyList<string>> GetChangedFilesAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
		public Task<string?> GetWorkingDirectoryDiffAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
		public Task<string?> GetCommitRangeDiffAsync(string workingDirectory, string fromCommit, string? toCommit = null, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
		public Task<GitDiffSummary?> GetDiffSummaryAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> CommitAllChangesAsync(string workingDirectory, string commitMessage, CancellationToken cancellationToken = default, GitCommitOptions? commitOptions = null) => throw new NotSupportedException();
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
		public Task<IReadOnlyList<string>> GetCommitLogAsync(string workingDirectory, string fromCommit, string? toCommit = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
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
