using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Inference;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class ReadOnlyConsultServiceTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly DbContextOptions<VibeSwarmDbContext> _dbOptions;

	public ReadOnlyConsultServiceTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();
		_dbOptions = new DbContextOptionsBuilder<VibeSwarmDbContext>().UseSqlite(_connection).Options;
		using var db = new VibeSwarmDbContext(_dbOptions);
		db.Database.EnsureCreated();
	}

	public void Dispose() => _connection.Dispose();

	[Fact]
	public async Task CliConsult_RunsWithWriteToolsBlocked_AndRecordsUsageAgainstTheJob()
	{
		var provider = new Provider { Id = Guid.NewGuid(), Name = "Claude", Type = ProviderType.Claude, IsEnabled = true };
		var instance = new RecordingProvider { Result = new ExecutionResult { Success = true, Output = "REVIEW_APPROVED", CostUsd = 0.02m } };
		var usage = new RecordingUsageService();
		var service = CreateService([provider], instance, usage: usage);
		var jobId = Guid.NewGuid();

		var result = await service.ConsultAsync(new ConsultRequest
		{
			Target = new ConsultTarget(provider.Id, null, "opus", "high"),
			Prompt = "Review this",
			SystemPrompt = "Be strict",
			WorkingDirectory = "/tmp/repo",
			JobId = jobId
		});

		Assert.True(result.Success);
		Assert.Equal("REVIEW_APPROVED", result.Text);
		Assert.Equal(0.02m, result.CostUsd);
		var run = Assert.Single(instance.Runs);
		Assert.Equal("opus", run.Options.Model);
		Assert.Equal("high", run.Options.ReasoningEffort);
		Assert.Equal("/tmp/repo", run.Options.WorkingDirectory);
		Assert.Equal("Be strict", run.Options.AppendSystemPrompt);
		Assert.Equal(ProviderPlanningHelper.PlanningDisallowedTools, run.Options.DisallowedTools);
		Assert.Equal((provider.Id, (Guid?)jobId), Assert.Single(usage.Recorded));
	}

	[Fact]
	public async Task CliConsult_IsSkipped_WhenTheProviderIsTurnedOff()
	{
		var provider = new Provider { Id = Guid.NewGuid(), Name = "Copilot", Type = ProviderType.Copilot, IsEnabled = false };
		var instance = new RecordingProvider();
		var service = CreateService([provider], instance);

		var result = await service.ConsultAsync(new ConsultRequest { Target = new ConsultTarget(provider.Id, null, null, null), Prompt = "Review this" });

		Assert.True(result.Skipped);
		Assert.False(result.Success);
		Assert.Empty(instance.Runs);
	}

	[Fact]
	public async Task CliConsult_IsSkipped_ForAProviderThatCannotRunReadOnly()
	{
		var provider = new Provider { Id = Guid.NewGuid(), Name = "OpenCode", Type = ProviderType.OpenCode, IsEnabled = true };
		var instance = new RecordingProvider();
		var service = CreateService([provider], instance);

		var result = await service.ConsultAsync(new ConsultRequest { Target = new ConsultTarget(provider.Id, null, null, null), Prompt = "Review this" });

		Assert.True(result.Skipped);
		Assert.Empty(instance.Runs);
	}

	[Fact]
	public async Task CliConsult_ReportsAFailure_WhenTheProviderGivesNoAnswer()
	{
		var provider = new Provider { Id = Guid.NewGuid(), Name = "Claude", Type = ProviderType.Claude, IsEnabled = true };
		var instance = new RecordingProvider { Result = new ExecutionResult { Success = false, ErrorMessage = "rate limited" } };
		var service = CreateService([provider], instance);

		var result = await service.ConsultAsync(new ConsultRequest { Target = new ConsultTarget(provider.Id, null, null, null), Prompt = "Review this" });

		Assert.False(result.Success);
		Assert.False(result.Skipped);
		Assert.Equal("rate limited", result.Error);
	}

	[Fact]
	public async Task InferenceConsult_SendsThePromptToTheChosenProviderAndModel()
	{
		var grok = new InferenceProvider { Id = Guid.NewGuid(), Name = "Grok", ProviderType = InferenceProviderType.Grok, Endpoint = "https://api.x.ai", IsEnabled = true };
		var inference = new ScriptedInference(_ => new InferenceResponse { Success = true, Response = " ISSUES:\n1. Broken ", CostUsd = 0.001m });
		var service = CreateService([], null, inference, [grok]);

		var result = await service.ConsultAsync(new ConsultRequest
		{
			Target = new ConsultTarget(null, grok.Id, "grok-4", null),
			Prompt = "Review this",
			SystemPrompt = "Be strict",
			TaskType = "review"
		});

		Assert.True(result.Success);
		Assert.Equal("ISSUES:\n1. Broken", result.Text);
		var request = Assert.Single(inference.Requests);
		Assert.Equal(grok.Id, request.ProviderId);
		Assert.Equal("grok-4", request.Model);
		Assert.Equal("Be strict", request.SystemPrompt);
		Assert.Equal("review", request.TaskType);
	}

	[Fact]
	public async Task Consult_IsSkipped_WhenNoTargetIsChosen()
	{
		var service = CreateService([], null);

		var result = await service.ConsultAsync(new ConsultRequest { Target = new ConsultTarget(null, null, null, null), Prompt = "Review this" });

		Assert.True(result.Skipped);
	}

	private ReadOnlyConsultService CreateService(
		IReadOnlyList<Provider> providers,
		IProvider? instance,
		IInferenceService? inference = null,
		IReadOnlyList<InferenceProvider>? inferenceProviders = null,
		IProviderUsageService? usage = null)
		=> new(
			new VibeSwarmDbContext(_dbOptions),
			new StubProviderService(providers, instance),
			inference ?? new ScriptedInference(_ => throw new InvalidOperationException("No inference expected")),
			new StubInferenceProviderService(inferenceProviders ?? []),
			usage ?? new RecordingUsageService(),
			NullLogger<ReadOnlyConsultService>.Instance);
}
