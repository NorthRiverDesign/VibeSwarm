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
	public void BuildCommitSubject_IgnoresIncompleteFirstPersonSummaryAndFallsBackToPrompt()
	{
		var subject = JobSummaryGenerator.BuildCommitSubject(
			sessionSummary: "I created an Account API Token for Cloudflare but pasting the key in",
			title: null,
			goalPrompt: "update Cloudflare API connection");

		Assert.Equal("Update Cloudflare API connection", subject);
	}

	[Fact]
	public void BuildCommitSubject_IgnoresPromptDerivedTitleAndUsesPromptHeadline()
	{
		const string scheduledPrompt = "fix scheduled job commit summaries so they stay short and stop listing src/VibeSwarm.Web/Services/JobScheduleProcessor.cs";

		var subject = JobSummaryGenerator.BuildCommitSubject(
			sessionSummary: null,
			title: scheduledPrompt,
			goalPrompt: scheduledPrompt);

		Assert.Equal("Fix scheduled job commit summaries so they stay short and stop listing", subject);
	}

	[Fact]
	public void BuildCommitSubject_IgnoresPromptDerivedSessionSummaryAndArtifacts()
	{
		const string scheduledPrompt = "fix scheduled job commit summaries so they stay short and stop listing src/VibeSwarm.Web/Services/JobScheduleProcessor.cs";

		var subject = JobSummaryGenerator.BuildCommitSubject(
			sessionSummary: """
				fix scheduled job commit summaries so they stay short and stop listing src/VibeSwarm.Web/Services/JobScheduleProcessor.cs | 2 files changed (+18/-6)

				Files:
				- src/VibeSwarm.Web/Services/JobScheduleProcessor.cs
				- src/VibeSwarm.Shared/Services/JobSummaryGenerator.cs
				""",
			title: scheduledPrompt,
			goalPrompt: scheduledPrompt);

		Assert.Equal("Fix scheduled job commit summaries so they stay short and stop listing", subject);
	}

	[Fact]
	public void BuildCommitSubject_IgnoresPromptDerivedCommitSummaryTagAndArtifacts()
	{
		const string scheduledPrompt = "fix scheduled job commit summaries so they stay short and stop listing src/VibeSwarm.Web/Services/JobScheduleProcessor.cs";

		var subject = JobSummaryGenerator.BuildCommitSubject(
			sessionSummary: null,
			title: scheduledPrompt,
			goalPrompt: scheduledPrompt,
			consoleOutput: """
				<commit-summary>
				fix scheduled job commit summaries so they stay short and stop listing src/VibeSwarm.Web/Services/JobScheduleProcessor.cs | 2 files changed (+18/-6)
				Files:
				- src/VibeSwarm.Web/Services/JobScheduleProcessor.cs
				- src/VibeSwarm.Shared/Services/JobSummaryGenerator.cs
				</commit-summary>
				""");

		Assert.Equal("Fix scheduled job commit summaries so they stay short and stop listing", subject);
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

	[Fact]
	public void GenerateSummary_ReturnsSingleLineWithoutFileStatsOrFileNames()
	{
		var summary = JobSummaryGenerator.GenerateSummary(
			gitDiff: """
				diff --git a/src/CloudflareProvider.php b/src/CloudflareProvider.php
				--- a/src/CloudflareProvider.php
				+++ b/src/CloudflareProvider.php
				diff --git a/tests/CloudflareProviderTest.php b/tests/CloudflareProviderTest.php
				--- a/tests/CloudflareProviderTest.php
				+++ b/tests/CloudflareProviderTest.php
				2 files changed, 20 insertions(+), 4 deletions(-)
				""",
			goalPrompt: "update Cloudflare API connection");

		Assert.NotNull(summary);
		Assert.Equal("Update Cloudflare API connection", summary);
		Assert.DoesNotContain("Files:", summary, StringComparison.Ordinal);
		Assert.DoesNotContain("file(s) changed", summary, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain('\n', summary!);
	}

	[Fact]
	public async Task PerformAutoCommitAsync_ScheduledJobIgnoresPromptDerivedSessionSummaryArtifacts()
	{
		var versionControl = new RecordingVersionControlService
		{
			HasUncommittedChangesResult = true,
			CommitResult = GitOperationResult.Succeeded(commitHash: "sched456")
		};

		var services = new ServiceCollection().BuildServiceProvider();
		var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
		var processor = new JobProcessingService(
			scopeFactory,
			NullLogger<JobProcessingService>.Instance,
			versionControl,
			projectEnvironmentCredentialService: new NoOpProjectEnvironmentCredentialService());

		const string scheduledPrompt = "fix scheduled job commit summaries so they stay short and stop listing src/VibeSwarm.Web/Services/JobScheduleProcessor.cs";
		var job = new Job
		{
			Id = Guid.NewGuid(),
			IsScheduled = true,
			Title = scheduledPrompt,
			GoalPrompt = scheduledPrompt,
			SessionSummary = """
				fix scheduled job commit summaries so they stay short and stop listing src/VibeSwarm.Web/Services/JobScheduleProcessor.cs | 2 files changed (+18/-6)

				Files:
				- src/VibeSwarm.Web/Services/JobScheduleProcessor.cs
				- src/VibeSwarm.Shared/Services/JobSummaryGenerator.cs
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

		var method = typeof(JobProcessingService).GetMethod("PerformAutoCommitAsync", BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);

		var task = (Task)method.Invoke(processor, [job, Path.GetTempPath(), false, CancellationToken.None])!;
		await task;

		Assert.Equal("Fix scheduled job commit summaries so they stay short and stop listing", versionControl.LastCommitMessage);
		Assert.Equal("sched456", job.GitCommitHash);
	}

	[Fact]
	public async Task PerformAutoCommitAsync_UsesInferenceCommitSummaryWhenConfigured()
	{
		var inferenceProviderId = Guid.NewGuid();
		var versionControl = new RecordingVersionControlService
		{
			HasUncommittedChangesResult = true,
			CommitResult = GitOperationResult.Succeeded(commitHash: "def5678"),
			ChangedFilesResult =
			[
				"src/VibeSwarm.Web/Services/JobProcessingService.Delivery.cs",
				"src/VibeSwarm.Shared/Data/Project.cs"
			]
		};
		var inferenceProvider = new InferenceProvider
		{
			Id = inferenceProviderId,
			Name = "Local Grok",
			ProviderType = VibeSwarm.Shared.Inference.InferenceProviderType.Grok,
			Endpoint = "https://inference.example",
			IsEnabled = true,
			Models =
			[
				new InferenceModel
				{
					InferenceProviderId = inferenceProviderId,
					ModelId = "grok-commit",
					IsAvailable = true,
					IsDefault = true,
					TaskType = "default"
				}
			]
		};
		var inferenceService = new RecordingInferenceService
		{
			Response = new VibeSwarm.Shared.Inference.InferenceResponse
			{
				Success = true,
				Response = "Use inference summaries for project auto-commit messages"
			}
		};

		var services = new ServiceCollection();
		services.AddSingleton<IInferenceProviderService>(new RecordingInferenceProviderService(inferenceProvider));
		services.AddSingleton<IInferenceService>(inferenceService);
		var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

		var processor = new JobProcessingService(
			scopeFactory,
			NullLogger<JobProcessingService>.Instance,
			versionControl,
			projectEnvironmentCredentialService: new NoOpProjectEnvironmentCredentialService());

		var job = new Job
		{
			Id = Guid.NewGuid(),
			Title = "Fallback title",
			GoalPrompt = "allow selecting an inference provider and model for commit summaries",
			SessionSummary = "provider summary should be ignored when inference is configured",
			Project = new Project
			{
				AutoCommitMode = AutoCommitMode.CommitOnly,
				CommitSummaryInferenceProviderId = inferenceProviderId,
				CommitSummaryInferenceModelId = "grok-commit"
			},
			Provider = new VibeSwarm.Shared.Providers.Provider
			{
				Name = "Copilot",
				Type = VibeSwarm.Shared.Providers.ProviderType.Copilot
			}
		};

		var method = typeof(JobProcessingService).GetMethod("PerformAutoCommitAsync", BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);

		var task = (Task)method.Invoke(processor, [job, Path.GetTempPath(), false, CancellationToken.None])!;
		await task;

		Assert.Equal("Use inference summaries for project auto-commit messages", versionControl.LastCommitMessage);
		Assert.NotNull(inferenceService.LastRequest);
		Assert.Equal("grok-commit", inferenceService.LastRequest!.Model);
		Assert.Contains("allow selecting an inference provider and model for commit summaries", inferenceService.LastRequest.Prompt);
		Assert.Contains("src/VibeSwarm.Web/Services/JobProcessingService.Delivery.cs", inferenceService.LastRequest.Prompt);
		Assert.Contains("src/VibeSwarm.Shared/Data/Project.cs", inferenceService.LastRequest.Prompt);
	}

	[Fact]
	public async Task PerformAutoCommitAsync_ScheduledJobUsesProjectCommitSummaryInferenceSettings()
	{
		var inferenceProviderId = Guid.NewGuid();
		var versionControl = new RecordingVersionControlService
		{
			HasUncommittedChangesResult = true,
			CommitResult = GitOperationResult.Succeeded(commitHash: "sched123"),
			ChangedFilesResult =
			[
				"src/VibeSwarm.Web/Services/JobScheduleProcessor.cs",
				"src/VibeSwarm.Shared/Services/JobSummaryGenerator.cs"
			]
		};
		var inferenceProvider = new InferenceProvider
		{
			Id = inferenceProviderId,
			Name = "Local Grok",
			ProviderType = VibeSwarm.Shared.Inference.InferenceProviderType.Grok,
			Endpoint = "https://inference.example",
			IsEnabled = true,
			Models =
			[
				new InferenceModel
				{
					InferenceProviderId = inferenceProviderId,
					ModelId = "grok-commit",
					IsAvailable = true,
					IsDefault = true,
					TaskType = "default"
				}
			]
		};
		var inferenceService = new RecordingInferenceService
		{
			Response = new VibeSwarm.Shared.Inference.InferenceResponse
			{
				Success = true,
				Response = "Keep scheduled job commit summaries concise"
			}
		};

		var services = new ServiceCollection();
		services.AddSingleton<IInferenceProviderService>(new RecordingInferenceProviderService(inferenceProvider));
		services.AddSingleton<IInferenceService>(inferenceService);
		var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

		var processor = new JobProcessingService(
			scopeFactory,
			NullLogger<JobProcessingService>.Instance,
			versionControl,
			projectEnvironmentCredentialService: new NoOpProjectEnvironmentCredentialService());

		const string scheduledPrompt = "fix scheduled job commit summaries so they stay short and stop listing src/VibeSwarm.Web/Services/JobScheduleProcessor.cs";
		var job = new Job
		{
			Id = Guid.NewGuid(),
			IsScheduled = true,
			Title = scheduledPrompt,
			GoalPrompt = scheduledPrompt,
			SessionSummary = "prompt-derived fallback should be ignored when inference is configured",
			Project = new Project
			{
				AutoCommitMode = AutoCommitMode.CommitOnly,
				CommitSummaryInferenceProviderId = inferenceProviderId,
				CommitSummaryInferenceModelId = "grok-commit"
			},
			Provider = new VibeSwarm.Shared.Providers.Provider
			{
				Name = "Copilot",
				Type = VibeSwarm.Shared.Providers.ProviderType.Copilot
			}
		};

		var method = typeof(JobProcessingService).GetMethod("PerformAutoCommitAsync", BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);

		var task = (Task)method.Invoke(processor, [job, Path.GetTempPath(), false, CancellationToken.None])!;
		await task;

		Assert.Equal("Keep scheduled job commit summaries concise", versionControl.LastCommitMessage);
		Assert.NotNull(inferenceService.LastRequest);
		Assert.Equal("grok-commit", inferenceService.LastRequest!.Model);
		Assert.Contains("src/VibeSwarm.Web/Services/JobScheduleProcessor.cs", inferenceService.LastRequest.Prompt);
	}

	[Fact]
	public async Task PerformAutoCommitAsync_ScheduledJobUsesPromptHeadlineWhenTitleMatchesPrompt()
	{
		var versionControl = new RecordingVersionControlService
		{
			HasUncommittedChangesResult = true,
			CommitResult = GitOperationResult.Succeeded(commitHash: "fed4321")
		};

		var services = new ServiceCollection().BuildServiceProvider();
		var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
		var processor = new JobProcessingService(
			scopeFactory,
			NullLogger<JobProcessingService>.Instance,
			versionControl,
			projectEnvironmentCredentialService: new NoOpProjectEnvironmentCredentialService());

		const string scheduledPrompt = "fix scheduled job commit summaries so they stay short and stop listing src/VibeSwarm.Web/Services/JobScheduleProcessor.cs";
		var job = new Job
		{
			Id = Guid.NewGuid(),
			IsScheduled = true,
			Title = scheduledPrompt,
			GoalPrompt = scheduledPrompt,
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

		var method = typeof(JobProcessingService).GetMethod("PerformAutoCommitAsync", BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);

		var task = (Task)method.Invoke(processor, [job, Path.GetTempPath(), false, CancellationToken.None])!;
		await task;

		Assert.Equal("Fix scheduled job commit summaries so they stay short and stop listing", versionControl.LastCommitMessage);
		Assert.Equal("fed4321", job.GitCommitHash);
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
		public IReadOnlyList<string> ChangedFilesResult { get; set; } = [];
		public string? LastCommitMessage { get; private set; }
		public GitCommitOptions? LastCommitOptions { get; private set; }

		public Task<bool> IsGitAvailableAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> IsGitRepositoryAsync(string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<string?> GetCurrentCommitHashAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
		public Task<string?> GetCurrentBranchAsync(string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<string?> GetRemoteUrlAsync(string workingDirectory, string remoteName = "origin", CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> HasUncommittedChangesAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult(HasUncommittedChangesResult);
		public Task<GitWorkingTreeStatus> GetWorkingTreeStatusAsync(string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IReadOnlyList<string>> GetChangedFilesAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default) => Task.FromResult(ChangedFilesResult);
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

	private sealed class RecordingInferenceProviderService(InferenceProvider provider) : IInferenceProviderService
	{
		private readonly InferenceProvider _provider = provider;

		public Task<IEnumerable<InferenceProvider>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IEnumerable<InferenceProvider>>([_provider]);
		public Task<InferenceProvider?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<InferenceProvider?>(id == _provider.Id ? _provider : null);
		public Task<IEnumerable<InferenceProvider>> GetEnabledAsync(CancellationToken ct = default) => Task.FromResult<IEnumerable<InferenceProvider>>([_provider]);
		public Task<InferenceProvider> CreateAsync(InferenceProvider provider, CancellationToken ct = default) => throw new NotSupportedException();
		public Task<InferenceProvider> UpdateAsync(InferenceProvider provider, CancellationToken ct = default) => throw new NotSupportedException();
		public Task DeleteAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
		public Task<IEnumerable<InferenceModel>> GetModelsAsync(Guid providerId, CancellationToken ct = default) => Task.FromResult<IEnumerable<InferenceModel>>(_provider.Models);
		public Task<IEnumerable<InferenceModel>> RefreshModelsAsync(Guid providerId, CancellationToken ct = default) => throw new NotSupportedException();
		public Task SetModelForTaskAsync(Guid providerId, string modelId, string taskType, CancellationToken ct = default) => throw new NotSupportedException();
		public Task<InferenceModel?> GetModelForTaskAsync(string taskType, CancellationToken ct = default) => throw new NotSupportedException();
	}

	private sealed class RecordingInferenceService : IInferenceService
	{
		public VibeSwarm.Shared.Inference.InferenceRequest? LastRequest { get; private set; }
		public VibeSwarm.Shared.Inference.InferenceResponse Response { get; set; } = new();

		public Task<VibeSwarm.Shared.Inference.InferenceHealthResult> CheckHealthAsync(string? endpoint = null, VibeSwarm.Shared.Inference.InferenceProviderType? providerType = null, CancellationToken ct = default) => throw new NotSupportedException();
		public Task<List<VibeSwarm.Shared.Inference.DiscoveredModel>> GetAvailableModelsAsync(string? endpoint = null, VibeSwarm.Shared.Inference.InferenceProviderType? providerType = null, CancellationToken ct = default) => throw new NotSupportedException();

		public Task<VibeSwarm.Shared.Inference.InferenceResponse> GenerateAsync(VibeSwarm.Shared.Inference.InferenceRequest request, CancellationToken ct = default)
		{
			LastRequest = request;
			return Task.FromResult(Response);
		}

		public Task<VibeSwarm.Shared.Inference.InferenceResponse> GenerateForTaskAsync(string taskType, string prompt, string? systemPrompt = null, CancellationToken ct = default) => throw new NotSupportedException();
	}
}
