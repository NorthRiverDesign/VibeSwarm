using System.ComponentModel.DataAnnotations;
using System.IO;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class ProjectFoldersAndLinksTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly DbContextOptions<VibeSwarmDbContext> _dbOptions;
	private readonly string _dataProtectionDirectory;

	public ProjectFoldersAndLinksTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();
		_dbOptions = new DbContextOptionsBuilder<VibeSwarmDbContext>()
			.UseSqlite(_connection)
			.Options;
		_dataProtectionDirectory = Path.Combine(Path.GetTempPath(), $"vibeswarm-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_dataProtectionDirectory);

		using var dbContext = CreateDbContext();
		dbContext.Database.EnsureCreated();
	}

	[Fact]
	public async Task SetLinkedProjectsAsync_LinksBothWays_AndUnlinkingFromEitherSideRemovesBoth()
	{
		await using var dbContext = CreateDbContext();
		var service = CreateProjectService(dbContext);
		var api = await CreateProjectAsync(service, "Api");
		var web = await CreateProjectAsync(service, "Web");
		var docs = await CreateProjectAsync(service, "Docs");

		await service.SetLinkedProjectsAsync(api.Id, [web.Id, docs.Id, api.Id]);

		Assert.Equal(new[] { docs.Id, web.Id }.OrderBy(id => id), await GetLinkedIdsAsync(service, api.Id));
		Assert.Equal([api.Id], await GetLinkedIdsAsync(service, web.Id));
		Assert.Equal([api.Id], await GetLinkedIdsAsync(service, docs.Id));

		await service.SetLinkedProjectsAsync(web.Id, []);

		Assert.Equal([docs.Id], await GetLinkedIdsAsync(service, api.Id));
		Assert.Empty(await GetLinkedIdsAsync(service, web.Id));
		Assert.Equal(2, await dbContext.ProjectLinks.CountAsync());
	}

	[Fact]
	public async Task SetLinkedProjectsAsync_RejectsAProjectThatDoesNotExist()
	{
		await using var dbContext = CreateDbContext();
		var service = CreateProjectService(dbContext);
		var api = await CreateProjectAsync(service, "Api");

		var error = await Assert.ThrowsAsync<InvalidOperationException>(
			() => service.SetLinkedProjectsAsync(api.Id, [Guid.NewGuid()]));

		Assert.Contains("no longer exist", error.Message);
		Assert.Empty(await dbContext.ProjectLinks.ToListAsync());
	}

	[Fact]
	public async Task DeletingALinkedProject_RemovesItsLinks()
	{
		await using var dbContext = CreateDbContext();
		var service = CreateProjectService(dbContext);
		var api = await CreateProjectAsync(service, "Api");
		var web = await CreateProjectAsync(service, "Web");
		await service.SetLinkedProjectsAsync(api.Id, [web.Id]);

		await service.DeleteAsync(web.Id);

		Assert.Empty(await dbContext.ProjectLinks.ToListAsync());
	}

	[Fact]
	public async Task UpdateAsync_KeepsLinks_WhenTheSavedProjectCarriesNone()
	{
		await using var dbContext = CreateDbContext();
		var service = CreateProjectService(dbContext);
		var api = await CreateProjectAsync(service, "Api");
		var web = await CreateProjectAsync(service, "Web");
		await service.SetLinkedProjectsAsync(api.Id, [web.Id]);

		await service.UpdateAsync(new Project { Id = api.Id, Name = "Api", WorkingPath = "/tmp/api", IsActive = false });

		Assert.Equal([web.Id], await GetLinkedIdsAsync(service, api.Id));
	}

	[Fact]
	public async Task Folders_AreTrimmedAndUniqueIgnoringCase()
	{
		await using var dbContext = CreateDbContext();
		var service = CreateProjectService(dbContext);

		var folder = await service.CreateFolderAsync(new ProjectFolder { Name = "  Storefront  " });

		Assert.Equal("Storefront", folder.Name);
		await Assert.ThrowsAsync<InvalidOperationException>(
			() => service.CreateFolderAsync(new ProjectFolder { Name = "storefront" }));
		await Assert.ThrowsAsync<ValidationException>(
			() => service.CreateFolderAsync(new ProjectFolder { Name = "   " }));

		var renamed = await service.UpdateFolderAsync(new ProjectFolder { Id = folder.Id, Name = "Shop" });
		Assert.Equal("Shop", renamed.Name);
		Assert.Equal(["Shop"], (await service.GetFoldersAsync()).Select(f => f.Name));
	}

	[Fact]
	public async Task DeleteFolderAsync_KeepsItsProjectsOutsideAnyFolder()
	{
		await using var dbContext = CreateDbContext();
		var service = CreateProjectService(dbContext);
		var folder = await service.CreateFolderAsync(new ProjectFolder { Name = "Storefront" });
		var api = await CreateProjectAsync(service, "Api", folder.Id);
		Assert.Equal(folder.Id, api.FolderId);

		await service.DeleteFolderAsync(folder.Id);

		Assert.Empty(await service.GetFoldersAsync());
		var reloaded = await dbContext.Projects.AsNoTracking().SingleAsync(p => p.Id == api.Id);
		Assert.Null(reloaded.FolderId);
	}

	[Fact]
	public async Task UpdateAsync_DropsAFolderThatNoLongerExists()
	{
		await using var dbContext = CreateDbContext();
		var service = CreateProjectService(dbContext);
		var api = await CreateProjectAsync(service, "Api");

		var updated = await service.UpdateAsync(new Project { Id = api.Id, Name = "Api", WorkingPath = "/tmp/api", FolderId = Guid.NewGuid() });

		Assert.Null(updated.FolderId);
	}

	[Fact]
	public void BuildStructuredPrompt_ListsLinkedProjects()
	{
		var job = new Job
		{
			GoalPrompt = "Add a field to the orders API.",
			Project = new Project
			{
				Id = Guid.NewGuid(),
				Name = "Web",
				WorkingPath = "/src/web",
				Links =
				[
					new ProjectLink
					{
						LinkedProject = new Project
						{
							Id = Guid.NewGuid(),
							Name = "Api",
							WorkingPath = "/src/api",
							GitHubRepository = "acme/api",
							Description = "Orders <and> billing\nservice"
						}
					}
				]
			}
		};

		var prompt = PromptBuilder.BuildStructuredPrompt(job);

		Assert.Contains("<linked-projects>", prompt);
		Assert.Contains("  - Api: /src/api | GitHub: acme/api | Orders &lt;and&gt; billing service", prompt);
		Assert.Contains("Change files only in this project", prompt);
		Assert.True(prompt.IndexOf("</project>", StringComparison.Ordinal) < prompt.IndexOf("<linked-projects>", StringComparison.Ordinal));
	}

	[Fact]
	public void BuildStructuredPrompt_LeavesOutLinkedProjects_WhenThereAreNone()
	{
		var prompt = PromptBuilder.BuildStructuredPrompt(new Job
		{
			GoalPrompt = "Fix the build.",
			Project = new Project { Name = "Web", WorkingPath = "/src/web" }
		});

		Assert.DoesNotContain("<linked-projects>", prompt);
	}

	private static async Task<Project> CreateProjectAsync(ProjectService service, string name, Guid? folderId = null)
		=> await service.CreateAsync(new Project
		{
			Name = name,
			WorkingPath = $"/tmp/{name.ToLowerInvariant()}",
			FolderId = folderId
		});

	private static async Task<List<Guid>> GetLinkedIdsAsync(ProjectService service, Guid projectId)
	{
		var project = await service.GetByIdAsync(projectId);
		return project!.Links.Select(link => link.LinkedProjectId).OrderBy(id => id).ToList();
	}

	private VibeSwarmDbContext CreateDbContext() => new(_dbOptions);

	private ProjectService CreateProjectService(VibeSwarmDbContext dbContext)
	{
		var dataProtectionProvider = DataProtectionProvider.Create(new DirectoryInfo(_dataProtectionDirectory));
		return new ProjectService(dbContext, new ProjectEnvironmentCredentialService(dataProtectionProvider), new NoVersionControlService());
	}

	public void Dispose()
	{
		_connection.Dispose();
		if (Directory.Exists(_dataProtectionDirectory))
		{
			Directory.Delete(_dataProtectionDirectory, recursive: true);
		}
	}

	private sealed class NoVersionControlService : FakeVersionControlServiceBase;
}
