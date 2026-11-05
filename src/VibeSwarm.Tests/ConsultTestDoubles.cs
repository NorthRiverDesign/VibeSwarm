using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Inference;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

/// <summary>A provider instance that records the one run it is asked for and returns a set result.</summary>
internal sealed class RecordingProvider : IProvider
{
	public Guid Id { get; init; } = Guid.NewGuid();
	public string Name { get; init; } = "Recording provider";
	public ProviderType Type { get; init; } = ProviderType.Claude;
	public ProviderConnectionMode ConnectionMode { get; init; } = ProviderConnectionMode.CLI;
	public bool IsConnected => true;
	public string? LastConnectionError => null;
	public ExecutionResult Result { get; set; } = new() { Success = true, Output = "ok" };
	public List<(string Prompt, ExecutionOptions Options)> Runs { get; } = [];

	public Task<ExecutionResult> ExecuteWithOptionsAsync(string prompt, ExecutionOptions options, IProgress<ExecutionProgress>? progress = null, CancellationToken cancellationToken = default)
	{
		Runs.Add((prompt, options));
		return Task.FromResult(Result);
	}

	public Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
	public Task<string> ExecuteAsync(string prompt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public Task<ExecutionResult> ExecuteWithSessionAsync(string prompt, string? sessionId = null, string? workingDirectory = null, IProgress<ExecutionProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public Task<ProviderInfo> GetProviderInfoAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public Task<UsageLimits> GetUsageLimitsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public Task<SessionSummary> GetSessionSummaryAsync(string? sessionId, string? workingDirectory = null, string? fallbackOutput = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public Task<PromptResponse> GetPromptResponseAsync(string prompt, string? workingDirectory = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public Task<CliUpdateResult> UpdateCliAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public Task<UsageLimits?> RefreshUsageLimitsAsync(CancellationToken cancellationToken = default) => Task.FromResult<UsageLimits?>(null);
}

internal sealed class StubProviderService(IReadOnlyList<Provider> providers, IProvider? instance) : FakeProviderServiceBase
{
	public override Task<Provider?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
		=> Task.FromResult(providers.FirstOrDefault(p => p.Id == id));

	public override IProvider? CreateInstance(Provider config) => instance;
}

internal sealed class StubInferenceProviderService(IReadOnlyList<InferenceProvider> providers) : FakeInferenceProviderServiceBase
{
	public override Task<InferenceProvider?> GetByIdAsync(Guid id, CancellationToken ct = default)
		=> Task.FromResult(providers.FirstOrDefault(p => p.Id == id));
}

internal sealed class ScriptedInference(Func<InferenceRequest, InferenceResponse> respond) : IInferenceService
{
	public List<InferenceRequest> Requests { get; } = [];

	public Task<InferenceResponse> GenerateAsync(InferenceRequest request, CancellationToken ct = default)
	{
		Requests.Add(request);
		return Task.FromResult(respond(request));
	}

	public Task<InferenceHealthResult> ProbeAsync(InferenceProbeRequest request, CancellationToken ct = default) => throw new NotSupportedException();
	public Task<InferenceHealthResult> CheckHealthAsync(string? endpoint = null, InferenceProviderType? providerType = null, CancellationToken ct = default) => throw new NotSupportedException();
	public Task<List<DiscoveredModel>> GetAvailableModelsAsync(string? endpoint = null, InferenceProviderType? providerType = null, CancellationToken ct = default) => throw new NotSupportedException();
	public Task<InferenceResponse> GenerateForTaskAsync(string taskType, string prompt, string? systemPrompt = null, CancellationToken ct = default) => throw new NotSupportedException();
}

internal sealed class RecordingUsageService : IProviderUsageService
{
	public List<(Guid ProviderId, Guid? JobId)> Recorded { get; } = [];

	public Task RecordUsageAsync(Guid providerId, Guid? jobId, ExecutionResult executionResult, CancellationToken cancellationToken = default)
	{
		Recorded.Add((providerId, jobId));
		return Task.CompletedTask;
	}

	public Task<UsageExhaustionWarning?> CheckExhaustionAsync(Guid providerId, int warningThreshold = 80, CancellationToken cancellationToken = default) => Task.FromResult<UsageExhaustionWarning?>(null);
	public Task<ProviderUsageSummary?> GetUsageSummaryAsync(Guid providerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public Task<Dictionary<Guid, ProviderUsageSummary>> GetAllUsageSummariesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public Task<List<ProviderUsageRecord>> GetUsageHistoryAsync(Guid providerId, int limit = 100, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public Task UpdateVersionInfoAsync(Guid providerId, string version, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public Task<ProviderUsageSummary> ApplyDetectedLimitsAsync(Guid providerId, UsageLimits limits, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public Task ResetPeriodAsync(Guid providerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

/// <summary>A consult service that answers from a script, for code that asks a second model.</summary>
internal sealed class ScriptedConsultService(Func<ConsultRequest, ConsultResult> respond) : IReadOnlyConsultService
{
	public List<ConsultRequest> Requests { get; } = [];

	public Task<ConsultResult> ConsultAsync(ConsultRequest request, CancellationToken cancellationToken = default)
	{
		Requests.Add(request);
		return Task.FromResult(respond(request));
	}
}
