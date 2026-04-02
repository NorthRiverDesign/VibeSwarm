using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.VersionControl;
using VibeSwarm.Shared.VersionControl.Models;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class JobSummaryGeneratorTests
{
	[Fact]
	public void BuildCommitSubject_UsesSingleCleanLineFromRichSessionSummary()
	{
		var subject = JobSummaryGenerator.BuildCommitSubject(
			"""
			Implement scheduler commit summary normalization

			Changes:
			• src/VibeSwarm.Web/Services/JobProcessingService.Delivery.cs
			• src/VibeSwarm.Tests/JobSummaryGeneratorTests.cs
			""",
			title: null,
			goalPrompt: null);

		Assert.Equal("Implement scheduler commit summary normalization", subject);
	}

	[Fact]
	public void BuildCommitSubject_SkipsArtifactLinesAndFallsBackToPrompt()
	{
		var subject = JobSummaryGenerator.BuildCommitSubject(
			"""
			Changes:
			• src/VibeSwarm.Web/Services/JobProcessingService.Delivery.cs
			• src/VibeSwarm.Tests/JobSummaryGeneratorTests.cs
			3 file(s) changed (+20/-4)
			Files: Services/* (2), Tests/* (1)
			""",
			title: null,
			goalPrompt: "fix scheduler auto commit summaries so they read like normal human commit messages");

		Assert.Equal("Fix scheduler auto commit summaries so they read like normal human", subject);
	}

	[Fact]
	public void BuildCommitSubject_PrefersCommitSummaryTagFromConsoleOutput()
	{
		var subject = JobSummaryGenerator.BuildCommitSubject(
			sessionSummary: """
				Changes:
				• noisy bullet
				""",
			title: "very long fallback title",
			goalPrompt: "fallback prompt",
			consoleOutput: """
				<commit-summary>
				refine scheduler auto-commit subjects for normal git history
				- file one
				- file two
				</commit-summary>
				""");

		Assert.Equal("Refine scheduler auto-commit subjects for normal git history", subject);
	}

	[Fact]
	public void BuildCommitSubject_StripsInlineDiffStatsFromSessionSummaryLine()
	{
		var subject = JobSummaryGenerator.BuildCommitSubject(
			"Fixed mobile UI issues - 3 files changed (+20/-4)",
			title: null,
			goalPrompt: "fix mobile UI issues");

		Assert.Equal("Fixed mobile UI issues", subject);
	}

	[Fact]
	public void BuildCommitSubject_StripsInlineFilePathNoiseFromSessionSummaryLine()
	{
		var subject = JobSummaryGenerator.BuildCommitSubject(
			"Implemented CRUD pages for Posts: src/VibeSwarm.Client/Pages/Posts.razor, src/VibeSwarm.Web/Controllers/PostsController.cs",
			title: null,
			goalPrompt: "implement CRUD pages for Posts");

		Assert.Equal("Implemented CRUD pages for Posts", subject);
	}

	[Fact]
	public void BuildCommitSubject_StripsInlineArtifactsFromCommitSummaryTag()
	{
		var subject = JobSummaryGenerator.BuildCommitSubject(
			sessionSummary: null,
			title: null,
			goalPrompt: "review middleware auth deficiencies",
			consoleOutput: """
				<commit-summary>
				Improved security by reviewing middleware auth deficiencies | 2 files changed (+12/-3)
				</commit-summary>
				""");

		Assert.Equal("Improved security by reviewing middleware auth deficiencies", subject);
	}

	[Fact]
	public async Task PerformAutoCommitAsync_UsesSanitizedCommitSubject()
	{
		var versionControl = new RecordingVersionControlService
		{
			HasUncommittedChangesResult = true,
			CommitResult = GitOperationResult.Succeeded(commitHash: "abc1234")
		};

		var services = new ServiceCollection().BuildServiceProvider();
		var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
		var processor = new JobProcessingService(
			scopeFactory,
			NullLogger<JobProcessingService>.Instance,
			versionControl,
			projectEnvironmentCredentialService: new NoOpProjectEnvironmentCredentialService());

		var job = new Job
		{
			Id = Guid.NewGuid(),
			Title = "Scheduler prompt fallback title",
			GoalPrompt = "fix scheduler auto commit summaries so they stay short",
			SessionSummary = """
				Fix scheduler auto-commit summaries

				Changes:
				• src/VibeSwarm.Web/Services/JobProcessingService.Delivery.cs
				• src/VibeSwarm.Shared/Services/JobSummaryGenerator.cs
				""",
			Project = new Project
			{
				AutoCommitMode = AutoCommitMode.CommitOnly,
				IdeasAutoCommit = false
			},
			Provider = new VibeSwarm.Shared.Providers.Provider
			{
				Name = "Copilot",
				Type = VibeSwarm.Shared.Providers.ProviderType.Copilot
			}
		};

		var workingDirectory = Path.GetTempPath();
		var method = typeof(JobProcessingService).GetMethod("PerformAutoCommitAsync", BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);

		var task = (Task)method.Invoke(processor, [job, workingDirectory, true, CancellationToken.None])!;
		await task;

		Assert.Equal("Fix scheduler auto-commit summaries", versionControl.LastCommitMessage);
		Assert.Equal("abc1234", job.GitCommitHash);
		Assert.NotNull(versionControl.LastCommitOptions);
		Assert.Contains("Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>", versionControl.LastCommitOptions!.MessageTrailers);
	}

	private sealed class NoOpProjectEnvironmentCredentialService : IProjectEnvironmentCredentialService
	{
		public void PrepareForStorage(Project project, IReadOnlyCollection<ProjectEnvironment>? existingEnvironments = null) { }
		public void PopulateForEditing(Project? project) { }
		public void PopulateForExecution(Project? project) { }
		public Dictionary<string, string>? BuildJobEnvironmentVariables(Project? project) => null;
	}

	private sealed class RecordingVersionControlService : IVersionControlService
	{
		public bool HasUncommittedChangesResult { get; set; }
		public GitOperationResult CommitResult { get; set; } = GitOperationResult.Succeeded();
		public string? LastCommitMessage { get; private set; }
		public GitCommitOptions? LastCommitOptions { get; private set; }

		public Task<bool> IsGitAvailableAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> IsGitRepositoryAsync(string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<string?> GetCurrentCommitHashAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
		public Task<string?> GetCurrentBranchAsync(string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<string?> GetRemoteUrlAsync(string workingDirectory, string remoteName = "origin", CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> HasUncommittedChangesAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult(HasUncommittedChangesResult);
		public Task<GitWorkingTreeStatus> GetWorkingTreeStatusAsync(string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IReadOnlyList<string>> GetChangedFilesAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<string?> GetWorkingDirectoryDiffAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<string?> GetCommitRangeDiffAsync(string workingDirectory, string fromCommit, string? toCommit = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitDiffSummary?> GetDiffSummaryAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> CommitAllChangesAsync(
			string workingDirectory,
			string commitMessage,
			CancellationToken cancellationToken = default,
			GitCommitOptions? commitOptions = null)
		{
			LastCommitMessage = commitMessage;
			LastCommitOptions = commitOptions;
			return Task.FromResult(CommitResult);
		}
		public Task<GitOperationResult> PushAsync(string workingDirectory, string remoteName = "origin", string? branchName = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> CommitAndPushAsync(string workingDirectory, string commitMessage, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> CreatePullRequestAsync(string workingDirectory, string sourceBranch, string targetBranch, string title, string? body = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> PreviewMergeBranchAsync(string workingDirectory, string sourceBranch, string targetBranch, string remoteName = "origin", CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> MergeBranchAsync(string workingDirectory, string sourceBranch, string targetBranch, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken cancellationToken = default, bool pushAfterMerge = true, IReadOnlyList<MergeConflictResolution>? conflictResolutions = null) => throw new NotSupportedException();
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
