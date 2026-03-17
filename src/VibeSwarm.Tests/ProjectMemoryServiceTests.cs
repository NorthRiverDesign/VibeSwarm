using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Shared.Data;
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
