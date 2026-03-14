using System.IO;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class ProjectEnvironmentFeatureTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly DbContextOptions<VibeSwarmDbContext> _dbOptions;
	private readonly string _dataProtectionDirectory;

	public ProjectEnvironmentFeatureTests()
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
	public async Task CreateAsync_EncryptsWebCredentials_AndHydratesClientFields()
	{
		await using var dbContext = CreateDbContext();
		var service = CreateProjectService(dbContext);

		var created = await service.CreateAsync(new Project
		{
			Name = "Environment Project",
			WorkingPath = "/tmp/environment-project",
			Environments =
			[
				new ProjectEnvironment
				{
					Name = "Staging",
					Type = EnvironmentType.Web,
					Url = "https://staging.example.com",
					Username = "tester@example.com",
					Password = "Sup3rSecret!"
				}
			]
		});

		var stored = await dbContext.ProjectEnvironments.AsNoTracking().SingleAsync();
		Assert.NotEqual("tester@example.com", stored.UsernameCiphertext);
		Assert.NotEqual("Sup3rSecret!", stored.PasswordCiphertext);

		var environment = Assert.Single(created.Environments);
		Assert.Equal("tester@example.com", environment.Username);
		Assert.True(environment.HasPassword);
		Assert.Null(environment.Password);
	}

	[Fact]
	public async Task UpdateAsync_BlankPasswordPreservesStoredSecret_AndClearPasswordRemovesIt()
	{
		await using var dbContext = CreateDbContext();
		var service = CreateProjectService(dbContext);

		var created = await service.CreateAsync(new Project
		{
			Name = "Environment Project",
			WorkingPath = "/tmp/environment-project",
			Environments =
			[
				new ProjectEnvironment
				{
					Name = "Preview",
					Type = EnvironmentType.Web,
					Url = "https://preview.example.com",
					Username = "preview-user",
					Password = "InitialPassword!"
				}
			]
		});

		var storedBefore = await dbContext.ProjectEnvironments.AsNoTracking().SingleAsync();
		var originalPasswordCiphertext = storedBefore.PasswordCiphertext;

		var environment = Assert.Single(created.Environments);
		environment.Password = null;
		environment.ClearPassword = false;
		created.Environments = [environment];
		await service.UpdateAsync(created);

		var storedAfterPreserve = await dbContext.ProjectEnvironments.AsNoTracking().SingleAsync();
		Assert.Equal(originalPasswordCiphertext, storedAfterPreserve.PasswordCiphertext);

		environment.ClearPassword = true;
		created.Environments = [environment];
		var updated = await service.UpdateAsync(created);

		var storedAfterClear = await dbContext.ProjectEnvironments.AsNoTracking().SingleAsync();
		Assert.Null(storedAfterClear.PasswordCiphertext);
		Assert.False(Assert.Single(updated.Environments).HasPassword);
	}

	[Fact]
	public void BuildStructuredPrompt_IncludesEnvironmentContextAndCredentials()
	{
		var job = new Job
		{
			GoalPrompt = "Verify the deployed app works.",
			Project = new Project
			{
				Name = "Web App",
				Description = "A deployed app",
				Environments =
				[
					new ProjectEnvironment
					{
						Name = "Staging",
						Type = EnvironmentType.Web,
						Url = "https://staging.example.com",
						Description = "Use the seeded admin account.",
						IsPrimary = true,
						IsEnabled = true,
						Username = "admin@example.com",
						Password = "StagingPassword!"
					},
					new ProjectEnvironment
					{
						Name = "Releases",
						Type = EnvironmentType.Release,
						Url = "https://github.com/octo/repo/releases",
						IsEnabled = true
					}
				]
			}
		};

		var prompt = PromptBuilder.BuildStructuredPrompt(job, true);

		Assert.Contains("<environments>", prompt);
		Assert.Contains("Prefer these URLs instead of assuming localhost.", prompt);
		Assert.Contains("https://staging.example.com", prompt);
		Assert.Contains("admin@example.com / StagingPassword!", prompt);
		Assert.Contains("https://github.com/octo/repo/releases", prompt);
	}

	[Fact]
	public async Task GenerateMcpConfigJsonAsync_AddsPlaywrightServerWhenWebEnvironmentExists()
	{
		var service = new McpConfigService(new FakeSkillService(
			new Skill { Id = Guid.NewGuid(), Name = "repo-map", Content = "Skill content", IsEnabled = true }));

		var json = await service.GenerateMcpConfigJsonAsync(new Project
		{
			Name = "Web App",
			WorkingPath = "/tmp/web-app",
			Environments =
			[
				new ProjectEnvironment
				{
					Name = "Production",
					Type = EnvironmentType.Web,
					Url = "https://app.example.com",
					IsEnabled = true
				}
			]
		});

		Assert.NotNull(json);
		Assert.Contains("\"playwright\"", json);
		Assert.Contains("@playwright/mcp@latest", json);
		Assert.Contains("\"repo-map\"", json);
	}

	[Fact]
	public async Task GenerateMcpConfigFileAsync_UsesOpenCodeShapeForOpenCodeProvider()
	{
		var service = new McpConfigService(new FakeSkillService());

		var filePath = await service.GenerateMcpConfigFileAsync(
			ProviderType.OpenCode,
			new Project
			{
				Name = "Web App",
				WorkingPath = "/tmp/web-app",
				Environments =
				[
					new ProjectEnvironment
					{
						Name = "Production",
						Type = EnvironmentType.Web,
						Url = "https://app.example.com",
						IsEnabled = true
					}
				]
			});

		Assert.NotNull(filePath);
		var json = await File.ReadAllTextAsync(filePath!);
		Assert.Contains("\"mcp\"", json);
		Assert.Contains("@playwright/mcp@latest", json);
	}

	[Fact]
	public async Task CreateAsync_MultipleEnvironments_NonPrimaryIsPrimary_False_Inserts()
	{
		// Regression: HasDefaultValue(false) on IsPrimary caused EF Core to omit
		// the column from INSERT when the value was false. The DB had no DEFAULT
		// clause, resulting in a NOT NULL constraint error (500).
		await using var dbContext = CreateDbContext();
		var service = CreateProjectService(dbContext);

		var created = await service.CreateAsync(new Project
		{
			Name = "Multi Env Project",
			WorkingPath = "/tmp/multi-env",
			Environments =
			[
				new ProjectEnvironment
				{
					Name = "Production",
					Type = EnvironmentType.Web,
					Url = "https://prod.example.com",
					IsPrimary = true,
					IsEnabled = true
				},
				new ProjectEnvironment
				{
					Name = "Staging",
					Type = EnvironmentType.Web,
					Url = "https://staging.example.com",
					IsPrimary = false,
					IsEnabled = true
				}
			]
		});

		var environments = await dbContext.ProjectEnvironments
			.AsNoTracking()
			.OrderBy(e => e.SortOrder)
			.ToListAsync();

		Assert.Equal(2, environments.Count);
		Assert.True(environments[0].IsPrimary);
		Assert.False(environments[1].IsPrimary);
	}

	[Fact]
	public async Task UpdateAsync_AddSecondEnvironment_NonPrimary_Succeeds()
	{
		// Regression: Adding a second environment where IsPrimary = false would
		// fail on INSERT due to missing DB DEFAULT for the IsPrimary column.
		await using var dbContext = CreateDbContext();
		var service = CreateProjectService(dbContext);

		var created = await service.CreateAsync(new Project
		{
			Name = "Single Env Project",
			WorkingPath = "/tmp/single-env",
			Environments =
			[
				new ProjectEnvironment
				{
					Name = "Production",
					Type = EnvironmentType.Web,
					Url = "https://prod.example.com",
					IsPrimary = true,
					IsEnabled = true
				}
			]
		});

		var env = Assert.Single(created.Environments);
		created.Environments = new List<ProjectEnvironment>
		{
			env,
			new ProjectEnvironment
			{
				Name = "Staging",
				Type = EnvironmentType.Web,
				Url = "https://staging.example.com",
				IsPrimary = false,
				IsEnabled = true
			}
		};

		var updated = await service.UpdateAsync(created);

		Assert.Equal(2, updated.Environments.Count);
		Assert.Single(updated.Environments, e => e.IsPrimary);
		Assert.Single(updated.Environments, e => !e.IsPrimary);
	}

	[Fact]
	public async Task UpdateAsync_AddFirstEnvironment_SeparateContexts_Succeeds()
	{
		// Simulate the real-world flow: project created in one request,
		// environment added in a separate request (different DbContext).
		Guid projectId;

		// Request 1: Create a project with no environments
		{
			await using var ctx = CreateDbContext();
			var svc = CreateProjectService(ctx);
			var created = await svc.CreateAsync(new Project
			{
				Name = "Fresh Project",
				WorkingPath = "/tmp/fresh"
			});
			projectId = created.Id;
		}

		// Request 2: Load the project, add an environment, save
		{
			await using var ctx = CreateDbContext();
			var svc = CreateProjectService(ctx);

			// Simulates what the client does: GET project, add env, PUT project
			var project = await svc.GetByIdAsync(projectId);
			Assert.NotNull(project);

			// Simulate a JSON round-trip (client serializes properties it knows about)
			var roundTripped = new Project
			{
				Id = project!.Id,
				Name = project.Name,
				Description = project.Description,
				WorkingPath = project.WorkingPath,
				IsActive = project.IsActive,
				IdeasAutoExpand = project.IdeasAutoExpand,
				ProviderSelections = project.ProviderSelections
					.Select(ps => new ProjectProvider
					{
						Id = ps.Id,
						ProjectId = ps.ProjectId,
						ProviderId = ps.ProviderId,
						Priority = ps.Priority,
						IsEnabled = ps.IsEnabled,
						PreferredModelId = ps.PreferredModelId,
						CreatedAt = ps.CreatedAt,
						UpdatedAt = ps.UpdatedAt
					}).ToList(),
				Environments = new List<ProjectEnvironment>
				{
					new()
					{
						Id = Guid.NewGuid(),
						Name = "Staging",
						Type = EnvironmentType.Web,
						Url = "https://staging.example.com",
						IsEnabled = true,
						IsPrimary = true
					}
				}
			};

			var updated = await svc.UpdateAsync(roundTripped);

			var env = Assert.Single(updated.Environments);
			Assert.Equal("Staging", env.Name);
			Assert.True(env.IsPrimary);
		}

		// Verify the environment is persisted
		{
			await using var ctx = CreateDbContext();
			var envs = await ctx.ProjectEnvironments.AsNoTracking().ToListAsync();
			Assert.Single(envs);
		}
	}

	[Fact]
	public async Task UpdateAsync_AddFirstEnvironment_WithProviderSelections_SeparateContexts_Succeeds()
	{
		// When saving environments, the client also sends back provider selections.
		// This test verifies that both collections are handled correctly in separate contexts.
		Guid projectId;
		Guid providerId;

		// Setup: Create a provider
		{
			await using var ctx = CreateDbContext();
			var provider = new Provider
			{
				Name = "Test Provider",
				Type = ProviderType.Claude,
				IsEnabled = true
			};
			ctx.Providers.Add(provider);
			await ctx.SaveChangesAsync();
			providerId = provider.Id;
		}

		// Request 1: Create a project with a provider selection
		{
			await using var ctx = CreateDbContext();
			var svc = CreateProjectService(ctx);
			var created = await svc.CreateAsync(new Project
			{
				Name = "Project With Provider",
				WorkingPath = "/tmp/with-provider",
				ProviderSelections =
				[
					new ProjectProvider
					{
						ProviderId = providerId,
						Priority = 0,
						IsEnabled = true
					}
				]
			});
			projectId = created.Id;
			Assert.Single(created.ProviderSelections);
		}

		// Request 2: Load the project, add an environment, send back with existing provider selections
		{
			await using var ctx = CreateDbContext();
			var svc = CreateProjectService(ctx);

			var project = await svc.GetByIdAsync(projectId);
			Assert.NotNull(project);

			// Simulate client round-trip: reconstruct from JSON-like data
			var roundTripped = new Project
			{
				Id = project!.Id,
				Name = project.Name,
				Description = project.Description,
				WorkingPath = project.WorkingPath,
				IsActive = project.IsActive,
				IdeasAutoExpand = project.IdeasAutoExpand,
				ProviderSelections = project.ProviderSelections
					.Select(ps => new ProjectProvider
					{
						Id = ps.Id,
						ProjectId = ps.ProjectId,
						ProviderId = ps.ProviderId,
						Priority = ps.Priority,
						IsEnabled = ps.IsEnabled,
						PreferredModelId = ps.PreferredModelId,
						CreatedAt = ps.CreatedAt,
						UpdatedAt = ps.UpdatedAt
					}).ToList(),
				Environments = new List<ProjectEnvironment>
				{
					new()
					{
						Id = Guid.NewGuid(),
						Name = "Production",
						Type = EnvironmentType.Web,
						Url = "https://prod.example.com",
						IsEnabled = true,
						IsPrimary = true
					}
				}
			};

			var updated = await svc.UpdateAsync(roundTripped);

			Assert.Single(updated.Environments);
			Assert.Single(updated.ProviderSelections);
		}
	}

	private VibeSwarmDbContext CreateDbContext() => new(_dbOptions);

	private ProjectService CreateProjectService(VibeSwarmDbContext dbContext)
	{
		var dataProtectionProvider = DataProtectionProvider.Create(new DirectoryInfo(_dataProtectionDirectory));
		var credentialService = new ProjectEnvironmentCredentialService(dataProtectionProvider);
		return new ProjectService(dbContext, credentialService);
	}

	public void Dispose()
	{
		_connection.Dispose();
		if (Directory.Exists(_dataProtectionDirectory))
		{
			Directory.Delete(_dataProtectionDirectory, recursive: true);
		}
	}

	private sealed class FakeSkillService(params Skill[] skills) : ISkillService
	{
		private readonly IReadOnlyList<Skill> _skills = skills;

		public Task<IEnumerable<Skill>> GetAllAsync(CancellationToken cancellationToken = default)
			=> Task.FromResult<IEnumerable<Skill>>(_skills);

		public Task<IEnumerable<Skill>> GetEnabledAsync(CancellationToken cancellationToken = default)
			=> Task.FromResult<IEnumerable<Skill>>(_skills.Where(skill => skill.IsEnabled));

		public Task<Skill?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
			=> Task.FromResult(_skills.FirstOrDefault(skill => skill.Id == id));

		public Task<Skill?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
			=> Task.FromResult(_skills.FirstOrDefault(skill => string.Equals(skill.Name, name, StringComparison.OrdinalIgnoreCase)));

		public Task<Skill> CreateAsync(Skill skill, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Skill> UpdateAsync(Skill skill, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> NameExistsAsync(string name, Guid? excludeId = null, CancellationToken cancellationToken = default)
			=> Task.FromResult(_skills.Any(skill => string.Equals(skill.Name, name, StringComparison.OrdinalIgnoreCase) && skill.Id != excludeId));
		public Task<string?> ExpandSkillAsync(string description, Guid providerId, string? modelId = null, CancellationToken cancellationToken = default)
			=> Task.FromResult<string?>(null);
	}
}
