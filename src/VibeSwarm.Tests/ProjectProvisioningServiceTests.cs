using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.Validation;
using VibeSwarm.Shared.VersionControl;
using VibeSwarm.Shared.VersionControl.Models;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class ProjectProvisioningServiceTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly DbContextOptions<VibeSwarmDbContext> _dbOptions;

	public ProjectProvisioningServiceTests()
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
	public async Task CreateProjectAsync_CloneMode_ClonesRepositoryAndPersistsProject()
	{
		await using var dbContext = CreateDbContext();
		var versionControl = new RecordingVersionControlService
		{
			GitHubCliAvailable = true,
			GitHubCliAuthenticated = true,
			GhCloneResult = GitOperationResult.Succeeded(output: "cloned")
		};
		var service = CreateProjectService(dbContext, versionControl);

		var created = await service.CreateProjectAsync(new ProjectCreationRequest
		{
			Mode = ProjectCreationMode.CloneGitHubRepository,
			Project = new Project
			{
				Name = "Cloned Project",
				WorkingPath = "/tmp/projects/cloned-project"
			},
			GitHub = new GitHubRepositoryOptions
			{
				Repository = "octocat/hello-world"
			}
		});

		Assert.Equal("octocat/hello-world", versionControl.CloneWithGitHubCliOwnerRepo);
		Assert.Equal("/tmp/projects/cloned-project", versionControl.CloneWithGitHubCliPath);
		Assert.Equal("octocat/hello-world", created.GitHubRepository);
		Assert.Equal("octocat/hello-world", await dbContext.Projects
			.Where(project => project.Id == created.Id)
			.Select(project => project.GitHubRepository)
			.SingleAsync());
	}

	[Fact]
	public async Task CreateProjectAsync_CreateGitHubMode_CreatesRepositoryAndStoresRemotePath()
	{
		await using var dbContext = CreateDbContext();
		var versionControl = new RecordingVersionControlService
		{
			CreateRepositoryResult = GitOperationResult.Succeeded(output: "created", remoteName: "origin"),
			RemoteUrl = "git@github.com:vibes/new-project.git",
			ExtractedRepository = "vibes/new-project"
		};
		var service = CreateProjectService(dbContext, versionControl);

		var created = await service.CreateProjectAsync(new ProjectCreationRequest
		{
			Mode = ProjectCreationMode.CreateGitHubRepository,
			Project = new Project
			{
				Name = "New Project",
				WorkingPath = "/tmp/projects/new-project"
			},
			GitHub = new GitHubRepositoryOptions
			{
				Repository = "new-project",
				Description = "Created from the project modal",
				IsPrivate = true,
				GitignoreTemplate = "CSharp",
				InitializeReadme = true
			}
		});

		Assert.Equal("/tmp/projects/new-project", versionControl.CreateRepositoryPath);
		Assert.Equal("new-project", versionControl.CreateRepositoryName);
		Assert.Equal("Created from the project modal", versionControl.CreateRepositoryDescription);
		Assert.True(versionControl.CreateRepositoryIsPrivate);
		Assert.Equal("CSharp", versionControl.CreateRepositoryGitignoreTemplate);
		Assert.True(versionControl.CreateRepositoryInitializeReadme);
		Assert.Equal("vibes/new-project", created.GitHubRepository);
	}

	[Fact]
	public async Task CreateProjectAsync_CloneMode_RejectsInvalidRepositoryFormat()
	{
		await using var dbContext = CreateDbContext();
		var versionControl = new RecordingVersionControlService();
		var service = CreateProjectService(dbContext, versionControl);

		var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateProjectAsync(new ProjectCreationRequest
		{
			Mode = ProjectCreationMode.CloneGitHubRepository,
			Project = new Project
			{
				Name = "Broken Clone",
				WorkingPath = "/tmp/projects/broken-clone"
			},
			GitHub = new GitHubRepositoryOptions
			{
				Repository = "not-a-valid-repository"
			}
		}));

		Assert.Contains("owner/repo", error.Message);
		Assert.Null(versionControl.CloneWithGitHubCliOwnerRepo);
		Assert.Null(versionControl.CloneRepositoryUrl);
	}

	[Fact]
	public async Task CreateAsync_RejectsPromptContextThatExceedsLimit()
	{
		await using var dbContext = CreateDbContext();
		var versionControl = new RecordingVersionControlService();
		var service = CreateProjectService(dbContext, versionControl);

		var error = await Assert.ThrowsAsync<System.ComponentModel.DataAnnotations.ValidationException>(() => service.CreateAsync(new Project
		{
			Name = "Prompt Heavy Project",
			WorkingPath = "/tmp/prompt-heavy-project",
			PromptContext = new string('p', ValidationLimits.ProjectPromptContextMaxLength + 1)
		}));

		Assert.Contains(nameof(Project.PromptContext), error.Message);
	}

	[Fact]
	public async Task CreateAsync_RejectsMemoryThatExceedsLimit()
	{
		await using var dbContext = CreateDbContext();
		var versionControl = new RecordingVersionControlService();
		var service = CreateProjectService(dbContext, versionControl);

		var error = await Assert.ThrowsAsync<System.ComponentModel.DataAnnotations.ValidationException>(() => service.CreateAsync(new Project
		{
			Name = "Memory Heavy Project",
			WorkingPath = "/tmp/memory-heavy-project",
			Memory = new string('m', ValidationLimits.ProjectMemoryMaxLength + 1)
		}));

		Assert.Contains(nameof(Project.Memory), error.Message);
	}

	private VibeSwarmDbContext CreateDbContext() => new(_dbOptions);

	private static ProjectService CreateProjectService(VibeSwarmDbContext dbContext, RecordingVersionControlService versionControl)
	{
		return new ProjectService(dbContext, new NoOpProjectEnvironmentCredentialService(), versionControl);
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

	private sealed class RecordingVersionControlService : FakeVersionControlServiceBase
	{
		public bool GitHubCliAvailable { get; set; }
		public bool GitHubCliAuthenticated { get; set; }
		public GitOperationResult GhCloneResult { get; set; } = GitOperationResult.Failed("Not configured");
		public GitOperationResult CloneResult { get; set; } = GitOperationResult.Failed("Not configured");
		public GitOperationResult CreateRepositoryResult { get; set; } = GitOperationResult.Failed("Not configured");
		public string? RemoteUrl { get; set; }
		public string? ExtractedRepository { get; set; }
		public string? CloneWithGitHubCliOwnerRepo { get; private set; }
		public string? CloneWithGitHubCliPath { get; private set; }
		public string? CloneRepositoryUrl { get; private set; }
		public string? CloneRepositoryPath { get; private set; }
		public string? CreateRepositoryPath { get; private set; }
		public string? CreateRepositoryName { get; private set; }
		public string? CreateRepositoryDescription { get; private set; }
		public bool CreateRepositoryIsPrivate { get; private set; }
		public string? CreateRepositoryGitignoreTemplate { get; private set; }
		public bool CreateRepositoryInitializeReadme { get; private set; }

		public override Task<bool> IsGitAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
		public override Task<bool> IsGitRepositoryAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult(false);
		public override Task<string?> GetCurrentCommitHashAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
		public override Task<string?> GetCurrentBranchAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
		public override Task<string?> GetRemoteUrlAsync(string workingDirectory, string remoteName = "origin", CancellationToken cancellationToken = default) => Task.FromResult(RemoteUrl);
		public override Task<bool> HasUncommittedChangesAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult(false);
		public override Task<IReadOnlyList<string>> GetChangedFilesAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
		public override Task<string?> GetWorkingDirectoryDiffAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
		public override Task<string?> GetCommitRangeDiffAsync(string workingDirectory, string fromCommit, string? toCommit = null, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
		public override Task<GitDiffSummary?> GetDiffSummaryAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default) => Task.FromResult<GitDiffSummary?>(null);
		public override Task<IReadOnlyList<GitBranchInfo>> GetBranchesAsync(string workingDirectory, bool includeRemote = true, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GitBranchInfo>>([]);

		public override Task<GitOperationResult> CloneRepositoryAsync(string repositoryUrl, string targetDirectory, string? branch = null, Action<string>? progressCallback = null, CancellationToken cancellationToken = default)
		{
			CloneRepositoryUrl = repositoryUrl;
			CloneRepositoryPath = targetDirectory;
			return Task.FromResult(CloneResult);
		}

		public override string GetGitHubCloneUrl(string ownerAndRepo, bool useSsh = true)
			=> useSsh ? $"git@github.com:{ownerAndRepo}.git" : $"https://github.com/{ownerAndRepo}.git";

		public override string? ExtractGitHubRepository(string? remoteUrl)
			=> remoteUrl == RemoteUrl ? ExtractedRepository : null;

		public override Task<bool> IsGitHubCliAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(GitHubCliAvailable);
		public override Task<bool> IsGitHubCliAuthenticatedAsync(CancellationToken cancellationToken = default) => Task.FromResult(GitHubCliAuthenticated);

		public override Task<GitOperationResult> CreateGitHubRepositoryAsync(string workingDirectory, string repositoryName, string? description = null, bool isPrivate = false, Action<string>? progressCallback = null, CancellationToken cancellationToken = default, string? gitignoreTemplate = null, string? licenseTemplate = null, bool initializeReadme = false)
		{
			CreateRepositoryPath = workingDirectory;
			CreateRepositoryName = repositoryName;
			CreateRepositoryDescription = description;
			CreateRepositoryIsPrivate = isPrivate;
			CreateRepositoryGitignoreTemplate = gitignoreTemplate;
			CreateRepositoryInitializeReadme = initializeReadme;
			return Task.FromResult(CreateRepositoryResult);
		}

		public override Task<GitWorkingTreeStatus> GetWorkingTreeStatusAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult(new GitWorkingTreeStatus());
		public override Task<IReadOnlyDictionary<string, string>> GetRemotesAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

		public override Task<GitOperationResult> CloneWithGitHubCliAsync(string ownerRepo, string targetDirectory, Action<string>? progressCallback = null, CancellationToken cancellationToken = default)
		{
			CloneWithGitHubCliOwnerRepo = ownerRepo;
			CloneWithGitHubCliPath = targetDirectory;
			return Task.FromResult(GhCloneResult);
		}

	}
}
