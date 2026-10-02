using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

/// <summary>
/// Covers the folder inspection that lets the new-project form answer for itself whether a
/// working directory is already a git repository, where it points, and what stack it uses.
/// </summary>
public sealed class WorkspaceInspectionTests : IDisposable
{
	private readonly string _root;
	private readonly FileSystemService _service = new();

	public WorkspaceInspectionTests()
	{
		_root = Path.Combine(Path.GetTempPath(), $"vibeswarm-inspect-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_root);
	}

	[Fact]
	public async Task InspectWorkspace_MissingDirectory_ReportsNotFoundButStillSuggestsAName()
	{
		var path = Path.Combine(_root, "not-created-yet");

		var inspection = await _service.InspectWorkspaceAsync(path);

		Assert.False(inspection.Exists);
		Assert.False(inspection.IsGitRepository);
		Assert.Equal("not-created-yet", inspection.SuggestedName);
	}

	[Fact]
	public async Task InspectWorkspace_EmptyDirectory_IsFlaggedEmpty()
	{
		var path = CreateDirectory("blank");

		var inspection = await _service.InspectWorkspaceAsync(path);

		Assert.True(inspection.Exists);
		Assert.True(inspection.IsEmpty);
		Assert.False(inspection.IsGitRepository);
	}

	[Fact]
	public async Task InspectWorkspace_GitRepositoryWithGitHubRemote_ResolvesRepositoryAndBranch()
	{
		var path = CreateGitRepository("api", "git@github.com:octocat/hello-world.git", "ref: refs/heads/develop");

		var inspection = await _service.InspectWorkspaceAsync(path);

		Assert.True(inspection.IsGitRepository);
		Assert.Equal("octocat/hello-world", inspection.GitHubRepository);
		Assert.Equal("develop", inspection.CurrentBranch);
	}

	[Fact]
	public async Task InspectWorkspace_HttpsRemote_ResolvesRepository()
	{
		var path = CreateGitRepository("web", "https://github.com/acme/web-app.git", "ref: refs/heads/main");

		var inspection = await _service.InspectWorkspaceAsync(path);

		Assert.Equal("acme/web-app", inspection.GitHubRepository);
		Assert.Equal("main", inspection.CurrentBranch);
	}

	[Fact]
	public async Task InspectWorkspace_NonGitHubRemote_KeepsUrlWithoutRepositorySlug()
	{
		var path = CreateGitRepository("internal", "https://gitlab.example.com/team/tool.git", "ref: refs/heads/main");

		var inspection = await _service.InspectWorkspaceAsync(path);

		Assert.True(inspection.IsGitRepository);
		Assert.Null(inspection.GitHubRepository);
		Assert.Equal("https://gitlab.example.com/team/tool.git", inspection.RemoteUrl);
	}

	[Fact]
	public async Task InspectWorkspace_DetachedHead_ReportsShortCommit()
	{
		var path = CreateGitRepository("detached", "https://github.com/acme/web-app.git", "0123456789abcdef0123456789abcdef01234567");

		var inspection = await _service.InspectWorkspaceAsync(path);

		Assert.Equal("0123456", inspection.CurrentBranch);
	}

	[Theory]
	[InlineData("Project.sln", ".NET", "dotnet build", "dotnet test")]
	[InlineData("package.json", "Node.js", "npm run build", "npm test")]
	[InlineData("Cargo.toml", "Rust", "cargo build", "cargo test")]
	[InlineData("go.mod", "Go", "go build ./...", "go test ./...")]
	public async Task InspectWorkspace_MarkerFile_SuggestsBuildAndTestCommands(
		string markerFile, string expectedStack, string expectedBuild, string expectedTest)
	{
		var path = CreateDirectory($"stack-{expectedStack.Replace('.', '-')}");
		await File.WriteAllTextAsync(Path.Combine(path, markerFile), string.Empty);

		var inspection = await _service.InspectWorkspaceAsync(path);

		Assert.Equal(expectedStack, inspection.DetectedStack);
		Assert.Equal(expectedBuild, inspection.SuggestedBuildCommand);
		Assert.Equal(expectedTest, inspection.SuggestedTestCommand);
	}

	[Fact]
	public async Task ScanWorkspaces_ReturnsSubdirectoriesAndSkipsHiddenOnes()
	{
		CreateGitRepository("repo-one", "https://github.com/acme/one.git", "ref: refs/heads/main");
		CreateDirectory("plain-folder");
		CreateDirectory(".hidden");

		var results = await _service.ScanWorkspacesAsync(_root);

		Assert.Equal(2, results.Count);
		Assert.Contains(results, r => r.SuggestedName == "repo-one" && r.IsGitRepository);
		Assert.Contains(results, r => r.SuggestedName == "plain-folder" && !r.IsGitRepository);
		Assert.DoesNotContain(results, r => r.SuggestedName == ".hidden");
	}

	[Fact]
	public async Task ScanWorkspaces_MissingRoot_ReturnsEmpty()
	{
		var results = await _service.ScanWorkspacesAsync(Path.Combine(_root, "nope"));

		Assert.Empty(results);
	}

	[Fact]
	public async Task ListDirectory_FlagsGitRepositories()
	{
		CreateGitRepository("tracked", "https://github.com/acme/tracked.git", "ref: refs/heads/main");
		CreateDirectory("untracked");

		var listing = await _service.ListDirectoryAsync(_root, directoriesOnly: true);

		Assert.True(listing.Entries.Single(e => e.Name == "tracked").IsGitRepository);
		Assert.False(listing.Entries.Single(e => e.Name == "untracked").IsGitRepository);
	}

	private string CreateDirectory(string name)
	{
		var path = Path.Combine(_root, name);
		Directory.CreateDirectory(path);
		return path;
	}

	private string CreateGitRepository(string name, string remoteUrl, string head)
	{
		var path = CreateDirectory(name);
		var gitPath = Path.Combine(path, ".git");
		Directory.CreateDirectory(gitPath);
		File.WriteAllText(Path.Combine(gitPath, "HEAD"), head);
		File.WriteAllText(Path.Combine(gitPath, "config"), $"""
			[core]
				repositoryformatversion = 0
			[remote "origin"]
				url = {remoteUrl}
				fetch = +refs/heads/*:refs/remotes/origin/*
			[branch "main"]
				remote = origin
			""");
		return path;
	}

	public void Dispose()
	{
		try
		{
			Directory.Delete(_root, recursive: true);
		}
		catch
		{
			// Best effort temp cleanup.
		}
	}
}
