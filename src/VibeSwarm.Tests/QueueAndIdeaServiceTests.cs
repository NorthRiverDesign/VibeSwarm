using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.VersionControl;
using VibeSwarm.Shared.VersionControl.Models;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class QueueAndIdeaServiceTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly DbContextOptions<VibeSwarmDbContext> _dbOptions;

	public QueueAndIdeaServiceTests()
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
	public async Task CreateAsync_AllowsQueueingAnotherJobWhileOneIsRunning()
	{
		await using var dbContext = CreateDbContext();
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Queue Project",
			WorkingPath = "/tmp/project"
		};
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Copilot",
			Type = ProviderType.Copilot,
			IsEnabled = true,
			IsDefault = true
		};

		dbContext.Projects.Add(project);
		dbContext.Providers.Add(provider);
		dbContext.Jobs.Add(new Job
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			ProviderId = provider.Id,
			GoalPrompt = "Existing running job",
			Status = JobStatus.Processing,
			StartedAt = DateTime.UtcNow.AddMinutes(-2)
		});
		await dbContext.SaveChangesAsync();

		var serviceProvider = new ServiceCollection().BuildServiceProvider();
		var jobService = new JobService(dbContext, serviceProvider);

		var createdJob = await jobService.CreateAsync(new Job
		{
			ProjectId = project.Id,
			ProviderId = provider.Id,
			GoalPrompt = "Queue the next job"
		});

		Assert.Equal(JobStatus.New, createdJob.Status);
		Assert.Equal(2, await dbContext.Jobs.CountAsync(j => j.ProjectId == project.Id));
		Assert.Equal(1, await dbContext.Jobs.CountAsync(j => j.ProjectId == project.Id && j.Status == JobStatus.New));
	}

	[Fact]
	public async Task UpdateAsync_UpdatesExistingIdeaWithoutCreatingDuplicate()
	{
		await using var dbContext = CreateDbContext();
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Ideas Project",
			WorkingPath = "/tmp/ideas"
		};
		var idea = new Idea
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			Description = "Original idea",
			SortOrder = 0,
			CreatedAt = DateTime.UtcNow
		};

		dbContext.Projects.Add(project);
		dbContext.Ideas.Add(idea);
		await dbContext.SaveChangesAsync();

		var ideaService = new IdeaService(
			dbContext,
			null!,
			null!,
			null!,
			NullLogger<IdeaService>.Instance);

		var updatedIdea = await ideaService.UpdateAsync(new Idea
		{
			Id = idea.Id,
			Description = "Updated idea",
			SortOrder = 0
		});

		Assert.Equal("Updated idea", updatedIdea.Description);
		Assert.Equal(1, await dbContext.Ideas.CountAsync(i => i.ProjectId == project.Id));
		Assert.Equal("Updated idea", await dbContext.Ideas
			.Where(i => i.Id == idea.Id)
			.Select(i => i.Description)
			.SingleAsync());
	}

	[Fact]
	public async Task GetByProjectIdAsync_IncludesLinkedQueuedJobState()
	{
		await using var dbContext = CreateDbContext();
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Queued Ideas Project",
			WorkingPath = "/tmp/queued-ideas"
		};
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Claude",
			Type = ProviderType.Claude,
			IsEnabled = true
		};
		var queuedJob = new Job
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			ProviderId = provider.Id,
			GoalPrompt = "Queued work item",
			Status = JobStatus.New
		};
		var queuedIdea = new Idea
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			Description = "Queued idea",
			JobId = queuedJob.Id,
			IsProcessing = true,
			SortOrder = 0,
			CreatedAt = DateTime.UtcNow
		};

		dbContext.Projects.Add(project);
		dbContext.Providers.Add(provider);
		dbContext.Jobs.Add(queuedJob);
		dbContext.Ideas.Add(queuedIdea);
		await dbContext.SaveChangesAsync();

		var ideaService = new IdeaService(
			dbContext,
			null!,
			null!,
			null!,
			NullLogger<IdeaService>.Instance);

		var ideas = (await ideaService.GetByProjectIdAsync(project.Id)).ToList();

		var loadedIdea = Assert.Single(ideas);
		Assert.NotNull(loadedIdea.Job);
		Assert.Equal(JobStatus.New, loadedIdea.Job!.Status);
	}

	[Fact]
	public async Task ConvertToJobAsync_UsesDirectImplementationPrompt_WhenProjectAutoExpandDisabled()
	{
		await using var dbContext = CreateDbContext();
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Direct Ideas Project",
			WorkingPath = "/tmp/direct-ideas",
			IdeasAutoExpand = false
		};
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Copilot",
			Type = ProviderType.Copilot,
			IsEnabled = true,
			IsDefault = true
		};
		var idea = new Idea
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			Description = "Implement a compact dashboard widget",
			SortOrder = 0
		};

		dbContext.Projects.Add(project);
		dbContext.Providers.Add(provider);
		dbContext.Ideas.Add(idea);
		await dbContext.SaveChangesAsync();

		var ideaService = CreateIdeaService(dbContext, provider);
		var job = await ideaService.ConvertToJobAsync(idea.Id);

		Assert.NotNull(job);
		Assert.Contains("Work directly from the idea below instead of first expanding it into a separate detailed specification.", job!.GoalPrompt);
		Assert.DoesNotContain("Begin by expanding this idea into a detailed specification, then implement it.", job.GoalPrompt);
	}

	[Fact]
	public async Task ConvertToJobAsync_UsesExpansionPrompt_WhenProjectAutoExpandEnabled()
	{
		await using var dbContext = CreateDbContext();
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Expanded Ideas Project",
			WorkingPath = "/tmp/expanded-ideas",
			IdeasAutoExpand = true
		};
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Claude",
			Type = ProviderType.Claude,
			IsEnabled = true,
			IsDefault = true
		};
		var idea = new Idea
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			Description = "Add project usage charts",
			SortOrder = 0
		};

		dbContext.Projects.Add(project);
		dbContext.Providers.Add(provider);
		dbContext.Ideas.Add(idea);
		await dbContext.SaveChangesAsync();

		var ideaService = CreateIdeaService(dbContext, provider);
		var job = await ideaService.ConvertToJobAsync(idea.Id);

		Assert.NotNull(job);
		Assert.Contains("Begin by expanding this idea into a detailed specification, then implement it.", job!.GoalPrompt);
	}

	[Fact]
	public async Task ConvertToJobAsync_PrefersApprovedExpansion_WhenProjectAutoExpandDisabled()
	{
		await using var dbContext = CreateDbContext();
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Approved Expansion Project",
			WorkingPath = "/tmp/approved-expansion",
			IdeasAutoExpand = false
		};
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "OpenCode",
			Type = ProviderType.OpenCode,
			IsEnabled = true,
			IsDefault = true
		};
		var idea = new Idea
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			Description = "Improve mobile navigation",
			ExpandedDescription = "Create a bottom navigation bar with clear project and job shortcuts.",
			ExpansionStatus = IdeaExpansionStatus.Approved,
			SortOrder = 0
		};

		dbContext.Projects.Add(project);
		dbContext.Providers.Add(provider);
		dbContext.Ideas.Add(idea);
		await dbContext.SaveChangesAsync();

		var ideaService = CreateIdeaService(dbContext, provider);
		var job = await ideaService.ConvertToJobAsync(idea.Id);

		Assert.NotNull(job);
		Assert.Contains("## Detailed Specification", job!.GoalPrompt);
		Assert.Contains(idea.ExpandedDescription, job.GoalPrompt);
		Assert.DoesNotContain("Work directly from the idea below", job.GoalPrompt);
	}

	[Fact]
	public async Task ProjectService_UpdateAsync_PersistsIdeasAutoExpandSetting()
	{
		await using var dbContext = CreateDbContext();
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Project Service Project",
			WorkingPath = "/tmp/project-service",
			IdeasAutoExpand = true
		};

		dbContext.Projects.Add(project);
		await dbContext.SaveChangesAsync();

		var projectService = new ProjectService(dbContext, new NoOpProjectEnvironmentCredentialService());
		var updated = await projectService.UpdateAsync(new Project
		{
			Id = project.Id,
			Name = project.Name,
			WorkingPath = project.WorkingPath,
			IdeasAutoExpand = false
		});

		Assert.False(updated.IdeasAutoExpand);
		Assert.False(await dbContext.Projects
			.Where(p => p.Id == project.Id)
			.Select(p => p.IdeasAutoExpand)
			.SingleAsync());
	}

	private VibeSwarmDbContext CreateDbContext()
	{
		return new VibeSwarmDbContext(_dbOptions);
	}

	private static IdeaService CreateIdeaService(VibeSwarmDbContext dbContext, Provider provider)
	{
		var serviceProvider = new ServiceCollection().BuildServiceProvider();
		var jobService = new JobService(dbContext, serviceProvider);
		var providerService = new FakeProviderService(provider);
		var versionControlService = new FakeVersionControlService();

		return new IdeaService(
			dbContext,
			jobService,
			providerService,
			versionControlService,
			NullLogger<IdeaService>.Instance);
	}

	public void Dispose()
	{
		_connection.Dispose();
	}

	private sealed class FakeProviderService(Provider provider) : IProviderService
	{
		private readonly Provider _provider = provider;

		public Task<IEnumerable<Provider>> GetAllAsync(CancellationToken cancellationToken = default)
			=> Task.FromResult<IEnumerable<Provider>>([_provider]);

		public Task<Provider?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
			=> Task.FromResult(id == _provider.Id ? _provider : null);

		public Task<Provider?> GetDefaultAsync(CancellationToken cancellationToken = default)
			=> Task.FromResult<Provider?>(_provider);

		public IProvider? CreateInstance(Provider config) => throw new NotSupportedException();
		public Task<Provider> CreateAsync(Provider provider, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Provider> UpdateAsync(Provider provider, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> TestConnectionAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<ConnectionTestResult> TestConnectionWithDetailsAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task SetEnabledAsync(Guid id, bool isEnabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task SetDefaultAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<SessionSummary> GetSessionSummaryAsync(Guid providerId, string? sessionId, string? workingDirectory = null, string? fallbackOutput = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IEnumerable<ProviderModel>> GetModelsAsync(Guid providerId, CancellationToken cancellationToken = default)
			=> Task.FromResult<IEnumerable<ProviderModel>>([]);
		public Task<IEnumerable<ProviderModel>> RefreshModelsAsync(Guid providerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task SetDefaultModelAsync(Guid providerId, Guid modelId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<CliUpdateResult> UpdateCliAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}

	private sealed class FakeVersionControlService : IVersionControlService
	{
		public Task<bool> IsGitAvailableAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> IsGitRepositoryAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult(false);
		public Task<string?> GetCurrentCommitHashAsync(string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<string?> GetCurrentBranchAsync(string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<string?> GetRemoteUrlAsync(string workingDirectory, string remoteName = "origin", CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> HasUncommittedChangesAsync(string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IReadOnlyList<string>> GetChangedFilesAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<string?> GetWorkingDirectoryDiffAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<string?> GetCommitRangeDiffAsync(string workingDirectory, string fromCommit, string? toCommit = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitDiffSummary?> GetDiffSummaryAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> CommitAllChangesAsync(string workingDirectory, string commitMessage, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> PushAsync(string workingDirectory, string remoteName = "origin", string? branchName = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> CommitAndPushAsync(string workingDirectory, string commitMessage, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IReadOnlyList<GitBranchInfo>> GetBranchesAsync(string workingDirectory, bool includeRemote = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> FetchAsync(string workingDirectory, string remoteName = "origin", bool prune = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> HardCheckoutBranchAsync(string workingDirectory, string branchName, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> SyncWithOriginAsync(string workingDirectory, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> CloneRepositoryAsync(string repositoryUrl, string targetDirectory, string? branch = null, Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public string GetGitHubCloneUrl(string ownerAndRepo, bool useSsh = true) => throw new NotSupportedException();
		public string? ExtractGitHubRepository(string? remoteUrl) => throw new NotSupportedException();
		public Task<GitOperationResult> CreateBranchAsync(string workingDirectory, string branchName, bool switchToBranch = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> DiscardAllChangesAsync(string workingDirectory, bool includeUntracked = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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

	private sealed class NoOpProjectEnvironmentCredentialService : IProjectEnvironmentCredentialService
	{
		public void PrepareForStorage(Project project, IReadOnlyCollection<ProjectEnvironment>? existingEnvironments = null) { }
		public void PopulateForEditing(Project? project) { }
		public void PopulateForExecution(Project? project) { }
	}
}
