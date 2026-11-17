using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Inference;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Services;

/// <summary>
/// Who answers a consult: a coding CLI run read-only (Claude or Copilot), or an inference
/// provider that sees only the prompt. Exactly one of the two ids is set.
/// </summary>
public sealed record ConsultTarget(Guid? ProviderId, Guid? InferenceProviderId, string? ModelId, string? ReasoningEffort)
{
	public bool IsSet => ProviderId.HasValue || InferenceProviderId.HasValue;
}

public sealed class ConsultRequest
{
	public required ConsultTarget Target { get; init; }
	public required string Prompt { get; init; }
	public string? SystemPrompt { get; init; }

	/// <summary>Where a CLI consult runs; it may read files there but never change them.</summary>
	public string? WorkingDirectory { get; init; }

	/// <summary>The job the usage is charged to, if any.</summary>
	public Guid? JobId { get; init; }

	public string? Title { get; init; }
	public string TaskType { get; init; } = "default";
	public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(20);
}

public sealed class ConsultResult
{
	public bool Success { get; init; }

	/// <summary>The consult never ran, for a reason that isn't a failure (usage hold, provider off).</summary>
	public bool Skipped { get; init; }

	public string? Text { get; init; }
	public string? Error { get; init; }
	public string? ProviderName { get; init; }
	public string? ModelUsed { get; init; }
	public int? InputTokens { get; init; }
	public int? OutputTokens { get; init; }
	public decimal? CostUsd { get; init; }

	public static ConsultResult Skip(string reason, string? providerName = null)
		=> new() { Skipped = true, Error = reason, ProviderName = providerName };
}

/// <summary>
/// Asks a second model a question about a repository without letting it change anything: the
/// reviewer of a job and the project Lead both go through here.
/// </summary>
public interface IReadOnlyConsultService
{
	Task<ConsultResult> ConsultAsync(ConsultRequest request, CancellationToken cancellationToken = default);
}

public sealed class ReadOnlyConsultService : IReadOnlyConsultService
{
	private readonly VibeSwarmDbContext _db;
	private readonly IProviderService _providers;
	private readonly IInferenceService _inference;
	private readonly IInferenceProviderService _inferenceProviders;
	private readonly IProviderUsageService _usage;
	private readonly ILogger<ReadOnlyConsultService> _logger;

	public ReadOnlyConsultService(
		VibeSwarmDbContext db,
		IProviderService providers,
		IInferenceService inference,
		IInferenceProviderService inferenceProviders,
		IProviderUsageService usage,
		ILogger<ReadOnlyConsultService> logger)
	{
		_db = db;
		_providers = providers;
		_inference = inference;
		_inferenceProviders = inferenceProviders;
		_usage = usage;
		_logger = logger;
	}

	public async Task<ConsultResult> ConsultAsync(ConsultRequest request, CancellationToken cancellationToken = default)
	{
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(request.Timeout);
		try
		{
			if (request.Target.ProviderId is Guid providerId)
			{
				return await ConsultCliAsync(providerId, request, timeout.Token);
			}

			if (request.Target.InferenceProviderId is Guid inferenceProviderId)
			{
				return await ConsultInferenceAsync(inferenceProviderId, request, timeout.Token);
			}

			return ConsultResult.Skip("No reviewer is chosen");
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return new ConsultResult { Error = $"No answer within {request.Timeout.TotalMinutes:0} minutes" };
		}
	}

	private async Task<ConsultResult> ConsultCliAsync(Guid providerId, ConsultRequest request, CancellationToken cancellationToken)
	{
		var provider = await _providers.GetByIdAsync(providerId, cancellationToken);
		if (provider == null || !provider.IsEnabled)
		{
			return ConsultResult.Skip("The chosen provider is missing or turned off", provider?.Name);
		}

		if (!ProviderPlanningHelper.SupportsPlanningMode(provider.Type))
		{
			return ConsultResult.Skip($"{provider.Name} can't run read-only", provider.Name);
		}

		var hold = await ProviderSessionLimitGuard.GetHoldAsync(_db, provider.Id, DateTime.UtcNow, cancellationToken);
		if (hold != null)
		{
			return ConsultResult.Skip($"{provider.Name} is near its usage limit", provider.Name);
		}

		var instance = _providers.CreateInstance(provider);
		if (instance == null)
		{
			return ConsultResult.Skip($"{provider.Name} could not be started", provider.Name);
		}

		var result = await instance.ExecuteWithOptionsAsync(
			request.Prompt,
			new ExecutionOptions
			{
				WorkingDirectory = request.WorkingDirectory,
				UseBareMode = provider.Type == ProviderType.Claude && provider.ConnectionMode == ProviderConnectionMode.CLI,
				Model = request.Target.ModelId,
				ReasoningEffort = request.Target.ReasoningEffort,
				Title = request.Title,
				AppendSystemPrompt = request.SystemPrompt,
				DisallowedTools = ProviderPlanningHelper.PlanningDisallowedTools,
				StrictMcpConfig = true,
				DisableBuiltinMcps = true
			},
			cancellationToken: cancellationToken);

		try
		{
			await _usage.RecordUsageAsync(provider.Id, request.JobId, result, CancellationToken.None);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Could not record consult usage for {Provider}", provider.Name);
		}

		var text = ProviderPlanningHelper.ExtractExecutionText(result)?.Trim();
		if (!result.Success || string.IsNullOrWhiteSpace(text))
		{
			return new ConsultResult
			{
				ProviderName = provider.Name,
				Error = string.IsNullOrWhiteSpace(result.ErrorMessage) ? $"{provider.Name} gave no answer" : result.ErrorMessage,
				CostUsd = result.CostUsd
			};
		}

		return new ConsultResult
		{
			Success = true,
			Text = text,
			ProviderName = provider.Name,
			ModelUsed = result.ModelUsed ?? request.Target.ModelId,
			InputTokens = result.InputTokens,
			OutputTokens = result.OutputTokens,
			CostUsd = result.CostUsd
		};
	}

	private async Task<ConsultResult> ConsultInferenceAsync(Guid inferenceProviderId, ConsultRequest request, CancellationToken cancellationToken)
	{
		var provider = await _inferenceProviders.GetByIdAsync(inferenceProviderId, cancellationToken);
		if (provider == null || !provider.IsEnabled)
		{
			return ConsultResult.Skip("The chosen inference provider is missing or turned off", provider?.Name);
		}

		var response = await _inference.GenerateAsync(new InferenceRequest
		{
			Prompt = request.Prompt,
			SystemPrompt = request.SystemPrompt,
			TaskType = request.TaskType,
			Model = request.Target.ModelId,
			ProviderId = provider.Id,
			ProviderType = provider.ProviderType,
			Endpoint = provider.Endpoint
		}, cancellationToken);

		var text = response.Response?.Trim();
		if (!response.Success || string.IsNullOrWhiteSpace(text))
		{
			return new ConsultResult
			{
				ProviderName = provider.Name,
				Error = string.IsNullOrWhiteSpace(response.Error) ? $"{provider.Name} gave no answer" : response.Error,
				CostUsd = response.CostUsd
			};
		}

		return new ConsultResult
		{
			Success = true,
			Text = text,
			ProviderName = provider.Name,
			ModelUsed = response.ModelUsed ?? request.Target.ModelId,
			InputTokens = response.PromptTokens,
			OutputTokens = response.CompletionTokens,
			CostUsd = response.CostUsd
		};
	}
}
