using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

public sealed class SkillStorageServiceTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly DbContextOptions<VibeSwarmDbContext> _dbOptions;
	private readonly string _rootPath;
	private readonly string? _previousOverride;

	public SkillStorageServiceTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();

		_dbOptions = new DbContextOptionsBuilder<VibeSwarmDbContext>()
			.UseSqlite(_connection)
			.Options;

		using var dbContext = CreateDbContext();
		dbContext.Database.EnsureCreated();

		_rootPath = Path.Combine(Path.GetTempPath(), "vibeswarm-tests", "skills-" + Guid.NewGuid().ToString("N"));
		_previousOverride = Environment.GetEnvironmentVariable("VIBESWARM_SKILLS_PATH");
		Environment.SetEnvironmentVariable("VIBESWARM_SKILLS_PATH", _rootPath);
	}

	[Fact]
	public void RootPath_HonorsEnvironmentOverride()
	{
		var storage = CreateStorage(CreateDbContext());
		Assert.Equal(Path.GetFullPath(_rootPath), storage.RootPath);
		Assert.True(Directory.Exists(storage.RootPath));
	}

	[Fact]
	public void GetSkillDirectory_UsesStableGuidLayout()
	{
		var storage = CreateStorage(CreateDbContext());
		var id = Guid.NewGuid();

		var directory = storage.GetSkillDirectory(id);
		Assert.Equal(Path.Combine(storage.RootPath, id.ToString("N")), directory);
		Assert.Equal(Path.Combine(directory, "SKILL.md"), storage.GetSkillManifestPath(id));
	}

	[Fact]
	public async Task EnsureMaterializedAsync_WritesContentForLegacySkillAndUpdatesStoragePath()
	{
		await using var dbContext = CreateDbContext();
		var skill = new Skill
		{
			Id = Guid.NewGuid(),
			Name = "legacy",
			Content = "# Legacy\nHello from the DB.",
		};
		dbContext.Skills.Add(skill);
		await dbContext.SaveChangesAsync();

		var storage = CreateStorage(dbContext);
		var materialized = await storage.EnsureMaterializedAsync(skill);

		var manifest = Path.Combine(materialized, "SKILL.md");
		Assert.True(File.Exists(manifest));
		Assert.Equal(skill.Content, await File.ReadAllTextAsync(manifest));

		var reloaded = await dbContext.Skills.SingleAsync(s => s.Id == skill.Id);
		Assert.Equal(materialized, reloaded.StoragePath);
	}

	[Fact]
	public async Task EnsureMaterializedAsync_IsIdempotentWhenFolderAlreadyExists()
	{
		await using var dbContext = CreateDbContext();
		var skill = new Skill
		{
			Id = Guid.NewGuid(),
			Name = "already-there",
			Content = "original content",
		};
		dbContext.Skills.Add(skill);
		await dbContext.SaveChangesAsync();

		var storage = CreateStorage(dbContext);
		var firstPath = await storage.EnsureMaterializedAsync(skill);
		var manifest = Path.Combine(firstPath, "SKILL.md");
		// Mutate the file the way a real install with references/ would leave additions behind.
		await File.WriteAllTextAsync(manifest, "already materialized content");

		var secondPath = await storage.EnsureMaterializedAsync(skill);

		Assert.Equal(firstPath, secondPath);
		Assert.Equal("already materialized content", await File.ReadAllTextAsync(manifest));
	}

	[Fact]
	public async Task MaterializeFromDirectoryAsync_CopiesFolderStructureAndReplacesExisting()
	{
		var stagingRoot = Path.Combine(Path.GetTempPath(), "vibeswarm-tests", "staging-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(stagingRoot);
		Directory.CreateDirectory(Path.Combine(stagingRoot, "references"));
		Directory.CreateDirectory(Path.Combine(stagingRoot, "scripts"));
		await File.WriteAllTextAsync(Path.Combine(stagingRoot, "SKILL.md"), "# Root");
		await File.WriteAllTextAsync(Path.Combine(stagingRoot, "references", "guide.md"), "# Guide");
		await File.WriteAllTextAsync(Path.Combine(stagingRoot, "scripts", "run.sh"), "#!/bin/sh\necho hi");

		await using var dbContext = CreateDbContext();
		var storage = CreateStorage(dbContext);
		var id = Guid.NewGuid();

		var destination = await storage.MaterializeFromDirectoryAsync(id, stagingRoot);
		Assert.True(File.Exists(Path.Combine(destination, "SKILL.md")));
		Assert.True(File.Exists(Path.Combine(destination, "references", "guide.md")));
		Assert.True(File.Exists(Path.Combine(destination, "scripts", "run.sh")));

		// Re-materializing from a different source wipes the old tree.
		var replacementRoot = Path.Combine(Path.GetTempPath(), "vibeswarm-tests", "replacement-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(replacementRoot);
		await File.WriteAllTextAsync(Path.Combine(replacementRoot, "SKILL.md"), "# Replaced");

		await storage.MaterializeFromDirectoryAsync(id, replacementRoot);
		Assert.Equal("# Replaced", await File.ReadAllTextAsync(Path.Combine(destination, "SKILL.md")));
		Assert.False(File.Exists(Path.Combine(destination, "references", "guide.md")));
		Assert.False(Directory.Exists(Path.Combine(destination, "references")));
	}

	[Fact]
	public async Task EnsureMaterializedAsync_ThenPromptBuilder_SurfacesPathForLegacySkill()
	{
		// Mirrors the runtime flow for a pre-install-metadata skill: the job processor
		// materializes the skill, the skill's StoragePath is updated, and the PromptBuilder
		// emits the SKILL.md path in the <available_skills> section.
		await using var dbContext = CreateDbContext();
		var skill = new Skill
		{
			Id = Guid.NewGuid(),
			Name = "legacy-lazy",
			Description = "Lazy-materialized legacy skill.",
			Content = "# Legacy\nInstructions inside.",
			IsEnabled = true
		};
		dbContext.Skills.Add(skill);
		await dbContext.SaveChangesAsync();

		var storage = CreateStorage(dbContext);
		await storage.EnsureMaterializedAsync(skill);

		// StoragePath is now populated; the prompt builder should render the absolute path.
		var agent = new Agent
		{
			Id = Guid.NewGuid(),
			Name = "Any",
			SkillLinks = [new AgentSkill { SkillId = skill.Id, Skill = skill }]
		};
		var job = new Job
		{
			GoalPrompt = "Work.",
			Project = new Project
			{
				Name = "P",
				WorkingPath = "/tmp/p",
				AgentAssignments =
				[
					new ProjectAgent { AgentId = agent.Id, Agent = agent }
				],
				Environments = []
			}
		};

		var prompt = PromptBuilder.BuildStructuredPrompt(job);
		Assert.Contains("legacy-lazy", prompt);
		Assert.Contains($"SKILL.md: {Path.Combine(skill.StoragePath!, "SKILL.md")}", prompt);
	}

	[Fact]
	public void Delete_RemovesFolderAndIsSafeWhenMissing()
	{
		using var dbContext = CreateDbContext();
		var storage = CreateStorage(dbContext);
		var id = Guid.NewGuid();
		var directory = storage.GetSkillDirectory(id);
		Directory.CreateDirectory(directory);
		File.WriteAllText(Path.Combine(directory, "SKILL.md"), "content");

		storage.Delete(id);
		Assert.False(Directory.Exists(directory));

		// Second call on a missing directory should not throw.
		storage.Delete(id);
	}

	private VibeSwarmDbContext CreateDbContext() => new(_dbOptions);

	private static SkillStorageService CreateStorage(VibeSwarmDbContext dbContext)
		=> new(dbContext, NullLogger<SkillStorageService>.Instance);

	public void Dispose()
	{
		Environment.SetEnvironmentVariable("VIBESWARM_SKILLS_PATH", _previousOverride);
		_connection.Dispose();

		try
		{
			if (Directory.Exists(_rootPath))
			{
				Directory.Delete(_rootPath, recursive: true);
			}
		}
		catch
		{
			// best-effort cleanup
		}
	}
}
