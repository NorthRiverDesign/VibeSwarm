using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.VersionControl;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class ProjectMemoryServiceTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly DbContextOptions<VibeSwarmDbContext> _dbOptions;
	private readonly string _workingDirectory;

	public ProjectMemoryServiceTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();

		_dbOptions = new DbContextOptionsBuilder<VibeSwarmDbContext>()
			.UseSqlite(_connection)
			.Options;

		using var dbContext = CreateDbContext();
		dbContext.Database.EnsureCreated();

		_workingDirectory = Path.Combine(Path.GetTempPath(), "vibeswarm-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_workingDirectory);
		Directory.CreateDirectory(Path.Combine(_workingDirectory, ".git", "info"));
		File.WriteAllText(Path.Combine(_workingDirectory, ".git", "info", "exclude"), string.Empty);
	}

	[Fact]
	public async Task PrepareAndSyncMemoryFile_RoundTripsProjectMemoryIntoDatabase()
	{
		var projectId = Guid.NewGuid();

		await using (var seedContext = CreateDbContext())
		{
			seedContext.Projects.Add(new Project
			{
				Id = projectId,
				Name = "Memory Project",
				WorkingPath = _workingDirectory,
				Memory = "Always run build verification."
			});
			await seedContext.SaveChangesAsync();
		}

		string? memoryFilePath;
		await using (var prepareContext = CreateDbContext())
		{
			var service = CreateService(prepareContext);
			var project = await prepareContext.Projects.SingleAsync(project => project.Id == projectId);
			memoryFilePath = await service.PrepareMemoryFileAsync(project);
		}

		Assert.NotNull(memoryFilePath);
		Assert.True(File.Exists(memoryFilePath));
		Assert.Contains(".vibeswarm/", await File.ReadAllTextAsync(Path.Combine(_workingDirectory, ".git", "info", "exclude")));

		await File.WriteAllTextAsync(memoryFilePath!, "Always run build verification.\nRemember to update migrations after schema changes.");

		await using (var syncContext = CreateDbContext())
		{
			var service = CreateService(syncContext);
			await service.SyncMemoryFromFileAsync(projectId, memoryFilePath);
		}

		await using var verifyContext = CreateDbContext();
		var persistedMemory = await verifyContext.Projects
			.Where(project => project.Id == projectId)
			.Select(project => project.Memory)
			.SingleAsync();

		Assert.Equal("Always run build verification.\nRemember to update migrations after schema changes.", persistedMemory);
	}

	[Fact]
	public async Task SyncMemoryFromFileAsync_EmptyFileClearsPersistedMemory()
	{
		var projectId = Guid.NewGuid();

		await using (var seedContext = CreateDbContext())
		{
			seedContext.Projects.Add(new Project
			{
				Id = projectId,
				Name = "Memory Project",
				WorkingPath = _workingDirectory,
				Memory = "Remember the deployment checklist."
			});
			await seedContext.SaveChangesAsync();
		}

		var memoryDirectory = Path.Combine(_workingDirectory, ".vibeswarm");
		Directory.CreateDirectory(memoryDirectory);
		var memoryFilePath = Path.Combine(memoryDirectory, "project-memory.md");
		await File.WriteAllTextAsync(memoryFilePath, "\n \r\n");

		await using (var syncContext = CreateDbContext())
		{
			var service = CreateService(syncContext);
			await service.SyncMemoryFromFileAsync(projectId, memoryFilePath);
		}

		await using var verifyContext = CreateDbContext();
		var persistedMemory = await verifyContext.Projects
			.Where(project => project.Id == projectId)
			.Select(project => project.Memory)
			.SingleAsync();

		Assert.Null(persistedMemory);
	}

	[Fact]
	public async Task EnsureGitExcludeAsync_HidesSessionArtifactsFromGitButNotProjectFiles()
	{
		var repository = Path.Combine(Path.GetTempPath(), "vibeswarm-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(repository);
		var git = new GitCommandExecutor();

		try
		{
			Assert.True((await git.ExecuteAsync("init", repository)).Success);

			await using (var dbContext = CreateDbContext())
			{
				await CreateService(dbContext).EnsureGitExcludeAsync(repository);
			}

			string[] artifacts =
			[
				".playwright-mcp/page-2026-10-01.png",
				"test-results/home/trace.zip",
				"src/Tests/TestResults/run.trx",
				"screenshot-mobile.png",
				"dotnet-test.log",
				"notes.tmp",
				"tmp/probe.js",
				"CLAUDE.local.md",
				"tasks/todo.md",
				"tasks/lessons.md"
			];
			string[] projectFiles =
			[
				"src/App.cs",
				"docs/screenshot-dashboard.png",
				"wwwroot/screenshot.png"
			];

			foreach (var path in artifacts.Concat(projectFiles))
			{
				var fullPath = Path.Combine(repository, path);
				Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
				await File.WriteAllTextAsync(fullPath, "content");
			}

			var status = await git.ExecuteAsync("status --porcelain=v1 --untracked-files=all", repository);
			Assert.True(status.Success);

			var untracked = status.Output
				.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Select(line => line[3..])
				.Order(StringComparer.Ordinal)
				.ToArray();

			Assert.Equal(projectFiles.Order(StringComparer.Ordinal).ToArray(), untracked);
		}
		finally
		{
			Directory.Delete(repository, recursive: true);
		}
	}

	[Fact]
	public async Task EnsureGitExcludeAsync_HidesSessionArtifactsInLinkedWorktree()
	{
		var root = Path.Combine(Path.GetTempPath(), "vibeswarm-tests", Guid.NewGuid().ToString("N"));
		var repository = Path.Combine(root, "main");
		var worktree = Path.Combine(root, "feature");
		Directory.CreateDirectory(repository);
		var git = new GitCommandExecutor();

		try
		{
			Assert.True((await git.ExecuteAsync("init", repository)).Success);
			Assert.True((await git.ExecuteAsync("-c user.name=Test -c user.email=test@example.com -c commit.gpgsign=false commit --allow-empty -m init", repository)).Success);
			Assert.True((await git.ExecuteAsync($"worktree add \"{worktree}\"", repository)).Success);

			await using (var dbContext = CreateDbContext())
			{
				await CreateService(dbContext).EnsureGitExcludeAsync(worktree);
			}

			foreach (var path in new[] { ".vibeswarm/project-memory.md", "tasks/todo.md", "App.cs" })
			{
				var fullPath = Path.Combine(worktree, path);
				Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
				await File.WriteAllTextAsync(fullPath, "content");
			}

			var status = await git.ExecuteAsync("status --porcelain=v1 --untracked-files=all", worktree);
			Assert.True(status.Success);
			Assert.Equal("?? App.cs", status.Output.Trim());
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	private VibeSwarmDbContext CreateDbContext() => new(_dbOptions);

	private static ProjectMemoryService CreateService(VibeSwarmDbContext dbContext)
		=> new(dbContext, NullLogger<ProjectMemoryService>.Instance);

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(_workingDirectory))
			{
				Directory.Delete(_workingDirectory, recursive: true);
			}
		}
		catch
		{
		}

		_connection.Dispose();
	}
}
