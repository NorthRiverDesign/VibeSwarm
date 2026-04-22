using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class JobServiceAgentPresetTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly DbContextOptions<VibeSwarmDbContext> _dbOptions;

	public JobServiceAgentPresetTests()
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
	public async Task CreateAsync_SelectedAgentAppliesAssignedProviderModelAndCycleDefaults()
	{
		await using var dbContext = CreateDbContext();
		var selectedProvider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "GitHub Copilot",
			Type = ProviderType.Copilot,
			ConnectionMode = ProviderConnectionMode.CLI,
			IsEnabled = true,
			IsDefault = true
		};
		var fallbackProvider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Claude Code",
			Type = ProviderType.Claude,
			ConnectionMode = ProviderConnectionMode.CLI,
			IsEnabled = true
		};
		var agent = new Agent
		{
			Id = Guid.NewGuid(),
			Name = "Security Reviewer",
			IsEnabled = true,
			DefaultProviderId = selectedProvider.Id,
			DefaultProvider = selectedProvider,
			DefaultModelId = "gpt-5.4",
			DefaultReasoningEffort = "high",
			DefaultCycleMode = CycleMode.Autonomous,
			DefaultCycleSessionMode = CycleSessionMode.ContinueSession,
			DefaultMaxCycles = 4,
			DefaultCycleReviewPrompt = "Review the previous cycle before continuing."
		};
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "VibeSwarm",
			WorkingPath = "/tmp/vibeswarm",
			ProviderSelections =
			[
				new ProjectProvider
				{
					ProviderId = selectedProvider.Id,
					Priority = 1,
					IsEnabled = true
				},
				new ProjectProvider
				{
					ProviderId = fallbackProvider.Id,
					Priority = 2,
					IsEnabled = true
				}
			],
			AgentAssignments =
			[
				new ProjectAgent
				{
					AgentId = agent.Id,
					Agent = agent,
					ProviderId = selectedProvider.Id,
					Provider = selectedProvider,
					PreferredModelId = "gpt-5.4",
					PreferredReasoningEffort = "high",
					IsEnabled = true
				}
			]
		};

		dbContext.Providers.AddRange(selectedProvider, fallbackProvider);
		dbContext.ProviderModels.AddRange(
			new ProviderModel
			{
				ProviderId = selectedProvider.Id,
				ModelId = "gpt-5.4",
				DisplayName = "GPT-5.4",
				IsAvailable = true,
				IsDefault = true
			},
			new ProviderModel
			{
				ProviderId = fallbackProvider.Id,
				ModelId = "claude-sonnet-4.6",
				DisplayName = "Claude Sonnet 4.6",
				IsAvailable = true,
				IsDefault = true
			});
		dbContext.Agents.Add(agent);
		dbContext.Projects.Add(project);
		await dbContext.SaveChangesAsync();

		var service = new JobService(dbContext, new ServiceCollection().BuildServiceProvider());
		var created = await service.CreateAsync(new Job
		{
			ProjectId = project.Id,
			GoalPrompt = "Review the current auth changes for security issues.",
			AgentId = agent.Id
		});

		Assert.Equal(selectedProvider.Id, created.ProviderId);
		Assert.Equal("gpt-5.4", created.ModelUsed);
		Assert.Equal("high", created.ReasoningEffort);
		Assert.Equal(CycleMode.Autonomous, created.CycleMode);
		Assert.Equal(CycleSessionMode.ContinueSession, created.CycleSessionMode);
		Assert.Equal(4, created.MaxCycles);
		Assert.Equal("Review the previous cycle before continuing.", created.CycleReviewPrompt);

		var targets = JsonSerializer.Deserialize<List<JobExecutionTarget>>(created.ExecutionPlan!);
		Assert.NotNull(targets);
		Assert.NotEmpty(targets);
		Assert.All(targets!, target => Assert.Equal(selectedProvider.Id, target.ProviderId));
		Assert.Equal("gpt-5.4", targets![0].ModelId);
	}

	[Fact]
	public async Task CreateAsync_SelectedAgentKeepsExplicitCycleOverrides()
	{
		await using var dbContext = CreateDbContext();
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "GitHub Copilot",
			Type = ProviderType.Copilot,
			ConnectionMode = ProviderConnectionMode.CLI,
			IsEnabled = true,
			IsDefault = true
		};
		var agent = new Agent
		{
			Id = Guid.NewGuid(),
			Name = "Implementation Agent",
			IsEnabled = true,
			DefaultProviderId = provider.Id,
			DefaultProvider = provider,
			DefaultModelId = "gpt-5.4",
			DefaultReasoningEffort = "medium",
			DefaultCycleMode = CycleMode.Autonomous,
			DefaultCycleSessionMode = CycleSessionMode.ContinueSession,
			DefaultMaxCycles = 6,
			DefaultCycleReviewPrompt = "Continue until the task is complete."
		};
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "VibeSwarm",
			WorkingPath = "/tmp/vibeswarm",
			ProviderSelections =
			[
				new ProjectProvider
				{
					ProviderId = provider.Id,
					Priority = 1,
					IsEnabled = true
				}
			],
			AgentAssignments =
			[
				new ProjectAgent
				{
					AgentId = agent.Id,
					Agent = agent,
					ProviderId = provider.Id,
					Provider = provider,
					PreferredModelId = "gpt-5.4",
					PreferredReasoningEffort = "medium",
					IsEnabled = true
				}
			]
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
		dbContext.Agents.Add(agent);
		dbContext.Projects.Add(project);
		await dbContext.SaveChangesAsync();

		var service = new JobService(dbContext, new ServiceCollection().BuildServiceProvider());
		var created = await service.CreateAsync(new Job
		{
			ProjectId = project.Id,
			GoalPrompt = "Implement the next feature iteration.",
			AgentId = agent.Id,
			CycleMode = CycleMode.FixedCount,
			CycleSessionMode = CycleSessionMode.FreshSession,
			MaxCycles = 2,
			CycleReviewPrompt = "Run exactly two passes."
		});

		Assert.Equal(provider.Id, created.ProviderId);
		Assert.Equal("gpt-5.4", created.ModelUsed);
		Assert.Equal("medium", created.ReasoningEffort);
		Assert.Equal(CycleMode.FixedCount, created.CycleMode);
		Assert.Equal(CycleSessionMode.FreshSession, created.CycleSessionMode);
		Assert.Equal(2, created.MaxCycles);
		Assert.Equal("Run exactly two passes.", created.CycleReviewPrompt);
	}

	[Fact]
	public async Task CreateAsync_SelectedAgentUsesInstructionsAsGoalPromptWhenPromptBlank()
	{
		await using var dbContext = CreateDbContext();
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "GitHub Copilot",
			Type = ProviderType.Copilot,
			ConnectionMode = ProviderConnectionMode.CLI,
			IsEnabled = true,
			IsDefault = true
		};
		var agent = new Agent
		{
			Id = Guid.NewGuid(),
			Name = "Implementation Agent",
			Responsibilities = "Implement the requested feature using the agent instructions.",
			IsEnabled = true,
			DefaultProviderId = provider.Id,
			DefaultProvider = provider
		};
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "VibeSwarm",
			WorkingPath = "/tmp/vibeswarm",
			ProviderSelections =
			[
				new ProjectProvider
				{
					ProviderId = provider.Id,
					Priority = 1,
					IsEnabled = true
				}
			]
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
		dbContext.Agents.Add(agent);
		dbContext.Projects.Add(project);
		await dbContext.SaveChangesAsync();

		var service = new JobService(dbContext, new ServiceCollection().BuildServiceProvider());
		var created = await service.CreateAsync(new Job
		{
			ProjectId = project.Id,
			GoalPrompt = "   ",
			AgentId = agent.Id
		});

		Assert.Equal(agent.Responsibilities, created.GoalPrompt);
		Assert.Equal(provider.Id, created.ProviderId);
		Assert.Equal(agent.Responsibilities, created.Title);
	}

	[Fact]
	public async Task CreateAsync_SelectedAgentWithoutFittingInstructions_RequiresExplicitGoalPrompt()
	{
		await using var dbContext = CreateDbContext();
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "GitHub Copilot",
			Type = ProviderType.Copilot,
			ConnectionMode = ProviderConnectionMode.CLI,
			IsEnabled = true,
			IsDefault = true
		};
		var agent = new Agent
		{
			Id = Guid.NewGuid(),
			Name = "Implementation Agent",
			Responsibilities = new string('a', 2001),
			IsEnabled = true,
			DefaultProviderId = provider.Id,
			DefaultProvider = provider
		};
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "VibeSwarm",
			WorkingPath = "/tmp/vibeswarm",
			ProviderSelections =
			[
				new ProjectProvider
				{
					ProviderId = provider.Id,
					Priority = 1,
					IsEnabled = true
				}
			]
		};

		dbContext.Providers.Add(provider);
		dbContext.Agents.Add(agent);
		dbContext.Projects.Add(project);
		await dbContext.SaveChangesAsync();

		var service = new JobService(dbContext, new ServiceCollection().BuildServiceProvider());
		var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(new Job
		{
			ProjectId = project.Id,
			GoalPrompt = string.Empty,
			AgentId = agent.Id
		}));

		Assert.Contains("selected agent instructions exceed 2000 characters", exception.Message);
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
