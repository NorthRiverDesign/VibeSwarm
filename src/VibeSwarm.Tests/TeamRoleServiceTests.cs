using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class TeamRoleServiceTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly DbContextOptions<VibeSwarmDbContext> _dbOptions;

	public TeamRoleServiceTests()
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
	public async Task CreateAsync_PersistsSelectedSkills()
	{
		await using var dbContext = CreateDbContext();
		var skill = new Skill
		{
			Id = Guid.NewGuid(),
			Name = "frontend-design",
			Content = "Create polished UI."
		};
		dbContext.Skills.Add(skill);
		await dbContext.SaveChangesAsync();

		var service = new TeamRoleService(dbContext);
		var created = await service.CreateAsync(new TeamRole
		{
			Name = "Front End Designer",
			Description = "Shapes the UI.",
			Responsibilities = "Own layout and interaction quality.",
			SkillLinks =
			[
				new TeamRoleSkill
				{
					SkillId = skill.Id
				}
			]
		});

		Assert.Single(created.SkillLinks);
		Assert.Equal(skill.Id, created.SkillLinks.Single().SkillId);
		Assert.Equal("frontend-design", created.SkillLinks.Single().Skill?.Name);
	}

	[Fact]
	public async Task UpdateAsync_ReplacesSkillAssignments()
	{
		await using var dbContext = CreateDbContext();
		var firstSkill = new Skill
		{
			Id = Guid.NewGuid(),
			Name = "project-management",
			Content = "Plan and coordinate."
		};
		var secondSkill = new Skill
		{
			Id = Guid.NewGuid(),
			Name = "deployment",
			Content = "Ship safely."
		};
		dbContext.Skills.AddRange(firstSkill, secondSkill);
		await dbContext.SaveChangesAsync();

		var service = new TeamRoleService(dbContext);
		var created = await service.CreateAsync(new TeamRole
		{
			Name = "Project Manager",
			SkillLinks =
			[
				new TeamRoleSkill
				{
					SkillId = firstSkill.Id
				}
			]
		});

		created.SkillLinks =
		[
			new TeamRoleSkill
			{
				SkillId = secondSkill.Id
			}
		];
		var updated = await service.UpdateAsync(created);

		Assert.Single(updated.SkillLinks);
		Assert.Equal(secondSkill.Id, updated.SkillLinks.Single().SkillId);
		Assert.Equal("deployment", updated.SkillLinks.Single().Skill?.Name);
	}

	[Fact]
	public async Task CreateAsync_PersistsDefaultProviderAndModel()
	{
		await using var dbContext = CreateDbContext();
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "GitHub Copilot",
			Type = ProviderType.Copilot,
			ConnectionMode = ProviderConnectionMode.CLI,
			IsEnabled = true
		};
		dbContext.Providers.Add(provider);
		dbContext.ProviderModels.Add(new ProviderModel
		{
			ProviderId = provider.Id,
			ModelId = "gpt-5.4",
			DisplayName = "GPT-5.4",
			IsAvailable = true,
			IsDefault = true
		});
		await dbContext.SaveChangesAsync();

		var service = new TeamRoleService(dbContext);
		var created = await service.CreateAsync(new TeamRole
		{
			Name = "Backend Engineer",
			DefaultProviderId = provider.Id,
			DefaultModelId = "gpt-5.4"
		});

		Assert.Equal(provider.Id, created.DefaultProviderId);
		Assert.Equal("gpt-5.4", created.DefaultModelId);
		Assert.Equal("GitHub Copilot", created.DefaultProvider?.Name);
	}

	[Fact]
	public async Task CreateAsync_PersistsNormalizedDefaultReasoningEffort()
	{
		await using var dbContext = CreateDbContext();
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "GitHub Copilot",
			Type = ProviderType.Copilot,
			ConnectionMode = ProviderConnectionMode.CLI,
			IsEnabled = true
		};
		dbContext.Providers.Add(provider);
		dbContext.ProviderModels.Add(new ProviderModel
		{
			ProviderId = provider.Id,
			ModelId = "gpt-5.4",
			DisplayName = "GPT-5.4",
			IsAvailable = true,
			IsDefault = true
		});
		await dbContext.SaveChangesAsync();

		var service = new TeamRoleService(dbContext);
		var created = await service.CreateAsync(new TeamRole
		{
			Name = "Reasoning Engineer",
			DefaultProviderId = provider.Id,
			DefaultModelId = "gpt-5.4",
			DefaultReasoningEffort = " High "
		});

		Assert.Equal("high", created.DefaultReasoningEffort);
	}

	[Fact]
	public async Task CreateAsync_WithDefaultProviderNavigation_DoesNotReinsertProvider()
	{
		await using var dbContext = CreateDbContext();
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "GitHub Copilot",
			Type = ProviderType.Copilot,
			ConnectionMode = ProviderConnectionMode.CLI,
			IsEnabled = true
		};
		dbContext.Providers.Add(provider);
		dbContext.ProviderModels.Add(new ProviderModel
		{
			ProviderId = provider.Id,
			ModelId = "gpt-5.4",
			DisplayName = "GPT-5.4",
			IsAvailable = true,
			IsDefault = true
		});
		await dbContext.SaveChangesAsync();

		var service = new TeamRoleService(dbContext);
		var created = await service.CreateAsync(new TeamRole
		{
			Name = "Backend Engineer",
			Description = "Builds durable backend features with clean, framework-first code.",
			Responsibilities = "Favor framework capabilities over custom abstractions.\nKeep services lean, explicit, and easy to maintain.",
			DefaultProviderId = provider.Id,
			DefaultProvider = new Provider
			{
				Id = provider.Id,
				Name = provider.Name,
				Type = provider.Type,
				ConnectionMode = provider.ConnectionMode,
				IsEnabled = provider.IsEnabled
			},
			DefaultModelId = "gpt-5.4"
		});

		Assert.Equal(provider.Id, created.DefaultProviderId);
		Assert.Equal("gpt-5.4", created.DefaultModelId);
		Assert.Equal("GitHub Copilot", created.DefaultProvider?.Name);
		Assert.Equal(1, await dbContext.Providers.CountAsync());
	}

	[Fact]
	public async Task CreateAsync_DefaultModelWithoutMatchingProvider_Throws()
	{
		await using var dbContext = CreateDbContext();
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "GitHub Copilot",
			Type = ProviderType.Copilot,
			ConnectionMode = ProviderConnectionMode.CLI,
			IsEnabled = true
		};
		dbContext.Providers.Add(provider);
		await dbContext.SaveChangesAsync();

		var service = new TeamRoleService(dbContext);

		var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(new TeamRole
		{
			Name = "Backend Engineer",
			DefaultProviderId = provider.Id,
			DefaultModelId = "gpt-5.4"
		}));

		Assert.Equal("The selected default model is not available for the chosen provider.", exception.Message);
	}

	[Fact]
	public async Task CreateAsync_DefaultReasoningWithoutSupportedProvider_Throws()
	{
		await using var dbContext = CreateDbContext();
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Unsupported Copilot REST",
			Type = ProviderType.Copilot,
			ConnectionMode = ProviderConnectionMode.REST,
			IsEnabled = true
		};
		dbContext.Providers.Add(provider);
		await dbContext.SaveChangesAsync();

		var service = new TeamRoleService(dbContext);

		var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(new TeamRole
		{
			Name = "Unsupported Reasoner",
			DefaultProviderId = provider.Id,
			DefaultReasoningEffort = "high"
		}));

		Assert.Equal("The selected default reasoning level is not supported by the chosen provider.", exception.Message);
	}

	private VibeSwarmDbContext CreateDbContext()
	{
		return new VibeSwarmDbContext(_dbOptions);
	}

	public void Dispose()
	{
		_connection.Dispose();
	}
}
