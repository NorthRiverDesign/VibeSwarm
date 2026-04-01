using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Inference;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Shared.Services;

public partial class IdeaService
{
	public async Task<SuggestIdeasResult> SuggestIdeasFromCodebaseAsync(Guid projectId, SuggestIdeasRequest? request = null, CancellationToken cancellationToken = default)
	{
		var normalizedRequest = NormalizeSuggestIdeasRequest(request);

		if (normalizedRequest.UseInference && _inferenceService == null)
		{
			_logger.LogWarning("Inference service is not configured for project {ProjectId}", projectId);
			return new SuggestIdeasResult
			{
				Stage = SuggestIdeasStage.NotConfigured,
				Message = "No inference service is configured. Add a provider under Settings → Inference."
			};
		}

		try
		{
			return await SuggestIdeasInternalAsync(projectId, normalizedRequest, cancellationToken);
		}
		catch (OperationCanceledException)
		{
			_logger.LogWarning("Codebase suggestion was cancelled for project {ProjectId}", projectId);
			return new SuggestIdeasResult
			{
				Stage = SuggestIdeasStage.GenerateFailed,
				Message = "The suggestion request was cancelled."
			};
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Unhandled error in SuggestIdeasFromCodebaseAsync for project {ProjectId}", projectId);
			return new SuggestIdeasResult
			{
				Stage = SuggestIdeasStage.GenerateFailed,
				Message = $"An unexpected error occurred: {ex.Message}",
				InferenceError = ex.ToString()
			};
		}
	}

	/// <summary>
	/// Internal implementation of SuggestIdeasFromCodebaseAsync with all per-stage error handling.
	/// The public method wraps this with a top-level safety net.
	/// </summary>
	private async Task<SuggestIdeasResult> SuggestIdeasInternalAsync(Guid projectId, SuggestIdeasRequest request, CancellationToken cancellationToken)
	{
		if (!request.ProviderId.HasValue && !string.IsNullOrWhiteSpace(request.ModelId))
		{
			return new SuggestIdeasResult
			{
				Stage = SuggestIdeasStage.ModelNotFound,
				Message = request.UseInference
					? "Choose an inference provider before selecting a specific model."
					: "Choose a provider before selecting a specific model."
			};
		}

		var project = await _dbContext.Projects
			.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);

		if (project == null || string.IsNullOrEmpty(project.WorkingPath))
		{
			_logger.LogWarning("Project {ProjectId} not found or has no working path", projectId);
			return new SuggestIdeasResult
			{
				Stage = SuggestIdeasStage.RepoMapFailed,
				Message = "Project not found or has no working directory configured."
			};
		}

		var repoMap = RepoMapGenerator.GenerateRepoMap(project.WorkingPath);
		if (string.IsNullOrEmpty(repoMap))
		{
			_logger.LogWarning("Repo map generation failed for project {ProjectId} at path {Path}", projectId, project.WorkingPath);
			return new SuggestIdeasResult
			{
				Stage = SuggestIdeasStage.RepoMapFailed,
				Message = $"Could not scan the project directory at \"{project.WorkingPath}\". Verify the path exists and is readable."
			};
		}

		var existingIdeas = await _dbContext.Ideas
			.Where(idea => idea.ProjectId == projectId)
			.OrderBy(idea => idea.SortOrder)
			.ThenBy(idea => idea.CreatedAt)
			.Select(idea => idea.Description)
			.ToListAsync(cancellationToken);

		var prompt = BuildCodebaseSuggestionPrompt(
			repoMap,
			project.Name,
			project.Description,
			project.PromptContext,
			request.IdeaCount,
			existingIdeas,
			request.AdditionalContext);
		const string systemPrompt = "You are a senior software engineer performing a codebase review. Identify concrete, actionable improvements. Return only a plain list of ideas, one per line starting with \"- \". No explanations or headers.";

		var generationResult = request.UseInference
			? await SuggestIdeasWithInferenceAsync(projectId, request, prompt, systemPrompt, cancellationToken)
			: await SuggestIdeasWithProviderAsync(projectId, project, request, prompt, cancellationToken);

		if (!generationResult.Result.Success)
		{
			return generationResult.Result;
		}

		var responseText = generationResult.ResponseText ?? string.Empty;
		var suggestions = ParseCodebaseSuggestions(responseText, request.IdeaCount);
		if (suggestions.Count == 0)
		{
			_logger.LogWarning("No parseable suggestions in inference response for project {ProjectId}. Raw response length: {Len}",
				projectId, responseText.Length);
			return new SuggestIdeasResult
			{
				Stage = SuggestIdeasStage.ParseFailed,
				Message = "The model responded but did not produce ideas in the expected format. Try a different model or re-run.",
				ModelUsed = generationResult.ModelUsed,
				InferenceDurationMs = generationResult.DurationMs,
				InferenceError = $"Raw response ({responseText.Length} chars): {responseText[..Math.Min(200, responseText.Length)]}…"
			};
		}

		var knownSuggestionKeys = new HashSet<string>(
			existingIdeas
			.Select(NormalizeIdeaDescriptionForDuplicateCheck)
			.Where(description => !string.IsNullOrEmpty(description)),
			StringComparer.OrdinalIgnoreCase);

		var createdIdeas = new List<Idea>();
		var skippedDuplicateCount = 0;
		foreach (var suggestion in suggestions)
		{
			var normalizedSuggestion = NormalizeIdeaDescriptionForDuplicateCheck(suggestion);
			if (string.IsNullOrEmpty(normalizedSuggestion) || !knownSuggestionKeys.Add(normalizedSuggestion))
			{
				skippedDuplicateCount++;
				continue;
			}

			try
			{
				var idea = await CreateAsync(new Idea
				{
					ProjectId = projectId,
					Description = suggestion
				}, cancellationToken);
				createdIdeas.Add(idea);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Failed to save suggested idea for project {ProjectId}", projectId);
			}
		}

		if (createdIdeas.Count == 0 && skippedDuplicateCount > 0)
		{
			return new SuggestIdeasResult
			{
				Success = true,
				Stage = SuggestIdeasStage.Success,
				Message = $"All {FormatIdeaCount(skippedDuplicateCount)} already exist for this project.",
				ModelUsed = generationResult.ModelUsed,
				InferenceDurationMs = generationResult.DurationMs
			};
		}

		if (createdIdeas.Count == 0)
		{
			return new SuggestIdeasResult
			{
				Stage = SuggestIdeasStage.GenerateFailed,
				Message = "Ideas were generated, but none could be saved. Check the logs and try again.",
				ModelUsed = generationResult.ModelUsed,
				InferenceDurationMs = generationResult.DurationMs
			};
		}

		_logger.LogInformation("Created {Count} suggested ideas for project {ProjectId} using model {Model}",
			createdIdeas.Count, projectId, generationResult.ModelUsed ?? "unknown");

		var duplicateMessageSuffix = skippedDuplicateCount == 0
			? "."
			: $" Skipped {skippedDuplicateCount} duplicate existing idea{(skippedDuplicateCount == 1 ? "" : "s")}.";
		var message = createdIdeas.Count == request.IdeaCount
			? $"{FormatIdeaCount(createdIdeas.Count)} added from codebase analysis{duplicateMessageSuffix}"
			: $"{createdIdeas.Count} of {request.IdeaCount} requested idea{(request.IdeaCount == 1 ? "" : "s")} added from codebase analysis{duplicateMessageSuffix}";

		return new SuggestIdeasResult
		{
			Success = true,
			Stage = SuggestIdeasStage.Success,
			Ideas = createdIdeas,
			Message = message,
			ModelUsed = generationResult.ModelUsed,
			InferenceDurationMs = generationResult.DurationMs
		};
	}

	private async Task<SuggestionGenerationResult> SuggestIdeasWithInferenceAsync(
		Guid projectId,
		SuggestIdeasRequest request,
		string prompt,
		string systemPrompt,
		CancellationToken cancellationToken)
	{
		InferenceProvider? selectedProvider = null;
		if (request.ProviderId.HasValue)
		{
			selectedProvider = await _dbContext.InferenceProviders
				.Include(provider => provider.Models)
				.FirstOrDefaultAsync(provider => provider.Id == request.ProviderId.Value && provider.IsEnabled, cancellationToken);

			if (selectedProvider == null)
			{
				_logger.LogWarning("Requested inference provider {ProviderId} was not found for project {ProjectId}", request.ProviderId.Value, projectId);
				return SuggestionGenerationResult.Fail(new SuggestIdeasResult
				{
					Stage = SuggestIdeasStage.ProviderNotFound,
					Message = "The selected inference provider is no longer available. Choose another provider and try again."
				});
			}
		}

		var providerDisplayName = selectedProvider?.Name ?? "Inference provider";
		var providerEndpoint = selectedProvider?.Endpoint;
		InferenceModel? selectedModel = null;

		if (selectedProvider != null)
		{
			if (!string.IsNullOrWhiteSpace(request.ModelId))
			{
				selectedModel = selectedProvider.Models
					.Where(model => model.IsAvailable)
					.FirstOrDefault(model => string.Equals(model.ModelId, request.ModelId, StringComparison.Ordinal));

				if (selectedModel == null)
				{
					return SuggestionGenerationResult.Fail(new SuggestIdeasResult
					{
						Stage = SuggestIdeasStage.ModelNotFound,
						Message = $"The selected model is no longer available for {selectedProvider.Name}. Choose another model and try again."
					});
				}
			}
			else
			{
				selectedModel = ResolveSuggestionModel(selectedProvider);
				if (selectedModel == null)
				{
					return SuggestionGenerationResult.Fail(new SuggestIdeasResult
					{
						Stage = SuggestIdeasStage.NoModel,
						Message = $"No model is assigned to the \"suggest\" or \"default\" task for {selectedProvider.Name}. Assign one under Settings → Inference."
					});
				}
			}
		}

		InferenceHealthResult health;
		try
		{
			health = await _inferenceService!.CheckHealthAsync(providerEndpoint, selectedProvider?.ProviderType, cancellationToken);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Health check failed for project {ProjectId} suggestion using provider {Provider}", projectId, providerDisplayName);
			return SuggestionGenerationResult.Fail(new SuggestIdeasResult
			{
				Stage = SuggestIdeasStage.ProviderUnreachable,
				Message = $"Could not reach {providerDisplayName}: {ex.Message}",
				InferenceError = ex.Message
			});
		}

		if (!health.IsAvailable)
		{
			var detail = string.IsNullOrEmpty(health.Error) ? "No error detail returned." : health.Error;
			_logger.LogWarning("{Provider} unavailable for project {ProjectId}: {Error}", providerDisplayName, projectId, detail);
			return SuggestionGenerationResult.Fail(new SuggestIdeasResult
			{
				Stage = SuggestIdeasStage.ProviderUnreachable,
				Message = $"{providerDisplayName} is not responding. {detail}",
				InferenceError = detail
			});
		}

		InferenceResponse inferenceResponse;
		try
		{
			_logger.LogInformation(
				"Sending codebase suggestion request to inference for project {ProjectId} using provider {Provider} requesting {Count} ideas",
				projectId,
				providerDisplayName,
				request.IdeaCount);

			if (selectedProvider != null)
			{
				inferenceResponse = await _inferenceService!.GenerateAsync(new InferenceRequest
				{
					TaskType = "suggest",
					Prompt = prompt,
					SystemPrompt = systemPrompt,
					Endpoint = selectedProvider.Endpoint,
					Model = selectedModel!.ModelId,
					ProviderType = selectedProvider.ProviderType
				}, cancellationToken);
			}
			else
			{
				inferenceResponse = await _inferenceService!.GenerateForTaskAsync("suggest", prompt, systemPrompt, cancellationToken);
			}
		}
		catch (OperationCanceledException)
		{
			_logger.LogWarning("Codebase suggestion request timed out for project {ProjectId}", projectId);
			return SuggestionGenerationResult.Fail(new SuggestIdeasResult
			{
				Stage = SuggestIdeasStage.GenerateFailed,
				Message = "The inference request timed out. Try a smaller or faster model, or increase the client timeout."
			});
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Inference request failed for project {ProjectId} suggestion", projectId);
			return SuggestionGenerationResult.Fail(new SuggestIdeasResult
			{
				Stage = SuggestIdeasStage.GenerateFailed,
				Message = $"Inference request failed: {ex.Message}",
				InferenceError = ex.Message
			});
		}

		if (!inferenceResponse.Success || string.IsNullOrWhiteSpace(inferenceResponse.Response))
		{
			var error = inferenceResponse.Error ?? "The model returned an empty response.";
			_logger.LogWarning("Inference returned no usable response for project {ProjectId}: {Error}", projectId, error);

			var isNoModel = error.Contains("No model configured", StringComparison.OrdinalIgnoreCase)
				|| error.Contains("model", StringComparison.OrdinalIgnoreCase) && error.Contains("configure", StringComparison.OrdinalIgnoreCase);

			return SuggestionGenerationResult.Fail(new SuggestIdeasResult
			{
				Stage = isNoModel ? SuggestIdeasStage.NoModel : SuggestIdeasStage.GenerateFailed,
				Message = isNoModel
					? "No model is assigned to the \"suggest\" or \"default\" task. Go to Settings → Inference to assign a model."
					: $"The model did not return a usable response: {error}",
				ModelUsed = inferenceResponse.ModelUsed,
				InferenceDurationMs = inferenceResponse.DurationMs,
				InferenceError = error
			});
		}

		return SuggestionGenerationResult.Success(
			inferenceResponse.Response.Trim(),
			inferenceResponse.ModelUsed,
			inferenceResponse.DurationMs);
	}

	private async Task<SuggestionGenerationResult> SuggestIdeasWithProviderAsync(
		Guid projectId,
		Project project,
		SuggestIdeasRequest request,
		string prompt,
		CancellationToken cancellationToken)
	{
		Provider? provider;
		if (request.ProviderId.HasValue)
		{
			provider = await _providerService.GetByIdAsync(request.ProviderId.Value, cancellationToken);
			if (provider == null || !provider.IsEnabled)
			{
				_logger.LogWarning("Requested provider {ProviderId} was not found for project {ProjectId}", request.ProviderId.Value, projectId);
				return SuggestionGenerationResult.Fail(new SuggestIdeasResult
				{
					Stage = SuggestIdeasStage.ProviderNotFound,
					Message = "The selected provider is no longer available. Choose another provider and try again."
				});
			}
		}
		else
		{
			provider = await _providerService.GetDefaultAsync(cancellationToken);
		}

		if (provider == null)
		{
			return SuggestionGenerationResult.Fail(new SuggestIdeasResult
			{
				Stage = SuggestIdeasStage.NotConfigured,
				Message = "No enabled provider is configured. Add one under Settings → Providers."
			});
		}

		if (!provider.IsEnabled)
		{
			return SuggestionGenerationResult.Fail(new SuggestIdeasResult
			{
				Stage = SuggestIdeasStage.ProviderNotFound,
				Message = $"The selected provider ({provider.Name}) is disabled. Enable it or choose another provider."
			});
		}

		var selectedModelId = string.IsNullOrWhiteSpace(request.ModelId) ? null : request.ModelId.Trim();
		if (selectedModelId != null)
		{
			var availableModelIds = await _dbContext.ProviderModels
				.Where(model => model.ProviderId == provider.Id && model.IsAvailable)
				.Select(model => model.ModelId)
				.ToListAsync(cancellationToken);

			if (!availableModelIds.Contains(selectedModelId, StringComparer.Ordinal))
			{
				return SuggestionGenerationResult.Fail(new SuggestIdeasResult
				{
					Stage = SuggestIdeasStage.ModelNotFound,
					Message = $"The selected model is no longer available for {provider.Name}. Choose another model and try again."
				});
			}
		}
		else
		{
			selectedModelId = await _dbContext.ProviderModels
				.Where(model => model.ProviderId == provider.Id && model.IsAvailable && model.IsDefault)
				.OrderBy(model => model.DisplayName ?? model.ModelId)
				.Select(model => model.ModelId)
				.FirstOrDefaultAsync(cancellationToken);
		}

		var providerInstance = _providerService.CreateInstance(provider);
		if (providerInstance == null)
		{
			return SuggestionGenerationResult.Fail(new SuggestIdeasResult
			{
				Stage = SuggestIdeasStage.NotConfigured,
				Message = $"Could not create the configured provider instance for {provider.Name}."
			});
		}

		PromptResponse response;
		try
		{
			_logger.LogInformation(
				"Sending codebase suggestion request to provider {Provider} for project {ProjectId} requesting {Count} ideas",
				provider.Name,
				projectId,
				request.IdeaCount);
			response = await ExecuteProviderSuggestionAsync(
				providerInstance,
				provider,
				project.WorkingPath,
				prompt,
				selectedModelId,
				cancellationToken);
		}
		catch (OperationCanceledException)
		{
			_logger.LogWarning("Codebase suggestion request timed out for project {ProjectId} using provider {Provider}", projectId, provider.Name);
			return SuggestionGenerationResult.Fail(new SuggestIdeasResult
			{
				Stage = SuggestIdeasStage.GenerateFailed,
				Message = "The provider request timed out. Try again with a smaller or faster model."
			});
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Provider request failed for project {ProjectId} suggestion", projectId);
			return SuggestionGenerationResult.Fail(new SuggestIdeasResult
			{
				Stage = SuggestIdeasStage.ProviderUnreachable,
				Message = $"Could not run {provider.Name}: {ex.Message}",
				InferenceError = ex.Message
			});
		}

		if (!response.Success || string.IsNullOrWhiteSpace(response.Response))
		{
			var error = response.ErrorMessage ?? $"{provider.Name} returned an empty response.";
			return SuggestionGenerationResult.Fail(new SuggestIdeasResult
			{
				Stage = SuggestIdeasStage.GenerateFailed,
				Message = $"The provider did not return a usable response: {error}",
				ModelUsed = response.ModelUsed ?? selectedModelId,
				InferenceDurationMs = response.ElapsedMilliseconds,
				InferenceError = error
			});
		}

		return SuggestionGenerationResult.Success(
			response.Response.Trim(),
			response.ModelUsed ?? selectedModelId,
			response.ElapsedMilliseconds);
	}

	private async Task<PromptResponse> ExecuteProviderSuggestionAsync(
		IProvider providerInstance,
		Provider provider,
		string workingDirectory,
		string prompt,
		string? modelName,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(modelName))
		{
			return await providerInstance.GetPromptResponseAsync(prompt, workingDirectory, cancellationToken);
		}

		var result = await providerInstance.ExecuteWithOptionsAsync(
			prompt,
			new ExecutionOptions
			{
				WorkingDirectory = workingDirectory,
				UseBareMode = provider.Type == ProviderType.Claude
					&& provider.ConnectionMode == ProviderConnectionMode.CLI,
				Model = modelName
			},
			cancellationToken: cancellationToken);
		var responseText = ProviderPlanningHelper.ExtractExecutionText(result);
		if (result.Success && !string.IsNullOrWhiteSpace(responseText))
		{
			return PromptResponse.Ok(responseText.Trim(), model: result.ModelUsed ?? modelName);
		}

		var errorMessage = string.IsNullOrWhiteSpace(result.ErrorMessage)
			? $"{provider.Name} did not return a response"
			: result.ErrorMessage;
		return PromptResponse.Fail(errorMessage);
	}

	private static string BuildCodebaseSuggestionPrompt(
		string repoMap,
		string projectName,
		string? description,
		string? promptContext,
		int ideaCount,
		IReadOnlyCollection<string> existingIdeas,
		string? additionalContext)
	{
		var sb = new StringBuilder();

		sb.AppendLine($"<overview>");
		sb.AppendLine($"You are reviewing the \"{projectName}\" codebase to identify improvements and missing features.");
		sb.AppendLine("</overview>");

		if (!string.IsNullOrEmpty(description))
		{
			sb.AppendLine("<context>");
			sb.AppendLine($"Project description: {description}");
			sb.AppendLine("</context>");
		}

		if (!string.IsNullOrEmpty(promptContext))
		{
			sb.AppendLine("<project_instructions>");
			sb.AppendLine(promptContext);
			sb.AppendLine("</project_instructions>");
		}

		if (!string.IsNullOrWhiteSpace(additionalContext))
		{
			sb.AppendLine("<run_context>");
			sb.AppendLine(additionalContext.Trim());
			sb.AppendLine("</run_context>");
		}

		sb.AppendLine("<repository_structure>");
		sb.AppendLine(repoMap);
		sb.AppendLine("</repository_structure>");

		if (existingIdeas.Count > 0)
		{
			sb.AppendLine("<existing_ideas>");
			sb.AppendLine("Avoid duplicating or lightly rewording ideas that already exist for this project backlog:");
			foreach (var existingIdea in existingIdeas.Take(50))
			{
				sb.Append("- ");
				sb.AppendLine(existingIdea);
			}

			if (existingIdeas.Count > 50)
			{
				sb.AppendLine($"- ...and {existingIdeas.Count - 50} more existing ideas not shown");
			}

			sb.AppendLine("</existing_ideas>");
		}

		sb.AppendLine("<objective>");
		sb.AppendLine("Analyze the repository structure and identify areas for improvement.");
		sb.AppendLine("Return the highest-impact user-facing ideas first. Prioritize:");
		sb.AppendLine("- Missing features or UX improvements that would directly benefit users");
		sb.AppendLine("- Reliability, validation, performance, or security improvements users would notice");
		sb.AppendLine("- Fixes for workflows that are confusing, fragile, or error-prone in real usage");
		sb.AppendLine("De-prioritize development-only work such as adding tests, refactoring internals, or documentation updates unless it clearly unlocks a user-facing improvement.");
		sb.AppendLine("</objective>");

		sb.AppendLine("<goal>");
		sb.AppendLine($"Return exactly {ideaCount} concrete, actionable idea{(ideaCount == 1 ? "" : "s")}. Each idea must be a short description (1-2 sentences).");
		sb.AppendLine("Do not repeat, restate, or lightly rename existing project ideas. Prefer genuinely new work items.");
		sb.AppendLine("Format: one idea per line, each starting with \"- \". No headers, no explanations outside the list.");
		sb.AppendLine("Example:");
		sb.AppendLine("- Add input validation to the registration form to prevent invalid email addresses");
		sb.AppendLine("- Implement a retry mechanism for failed API calls to improve reliability");
		sb.AppendLine("</goal>");

		return sb.ToString();
	}

	/// <summary>
	/// Parses a list of suggestions from an LLM response.
	/// Handles common formats: "- item", "• item", and "1. item".
	/// </summary>
	private static List<string> ParseCodebaseSuggestions(string response, int maxSuggestions)
	{
		var suggestions = new List<string>();

		foreach (var rawLine in response.Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			var line = rawLine.Trim();

			string? extracted = null;

			if (line.StartsWith("- ") && line.Length > 2)
				extracted = line[2..].Trim();
			else if (line.StartsWith("• ") && line.Length > 2)
				extracted = line[2..].Trim();
			else if (line.Length > 3 && char.IsDigit(line[0]) && line[1] == '.' && line[2] == ' ')
				extracted = line[3..].Trim();
			else if (line.Length > 4 && char.IsDigit(line[0]) && char.IsDigit(line[1]) && line[2] == '.' && line[3] == ' ')
				extracted = line[4..].Trim();

			if (!string.IsNullOrWhiteSpace(extracted) && extracted.Length >= 10)
				suggestions.Add(extracted);
		}

		return suggestions
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.Select((suggestion, index) => new
			{
				Suggestion = suggestion,
				Index = index,
				Score = ScoreCodebaseSuggestion(suggestion)
			})
			.OrderByDescending(entry => entry.Score)
			.ThenBy(entry => entry.Index)
			.Take(maxSuggestions)
			.Select(entry => entry.Suggestion)
			.ToList();
	}

	private static string NormalizeIdeaDescriptionForDuplicateCheck(string? description)
	{
		if (string.IsNullOrWhiteSpace(description))
		{
			return string.Empty;
		}

		return string.Join(' ', description.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
	}

	private static string FormatIdeaCount(int count)
		=> $"{count} idea{(count == 1 ? "" : "s")}";

	private static int ScoreCodebaseSuggestion(string suggestion)
	{
		var normalizedSuggestion = suggestion.Trim().ToLowerInvariant();
		var score = 0;

		if (ContainsAny(normalizedSuggestion, UserImpactSuggestionKeywords))
		{
			score += 3;
		}

		if (ContainsAny(normalizedSuggestion, DevelopmentOnlySuggestionKeywords))
		{
			score -= 3;
		}

		if (StartsWithAny(normalizedSuggestion, DevelopmentOnlySuggestionPrefixes))
		{
			score -= 2;
		}

		return score;
	}

	private static bool ContainsAny(string text, IEnumerable<string> keywords)
	{
		foreach (var keyword in keywords)
		{
			if (text.Contains(keyword, StringComparison.Ordinal))
			{
				return true;
			}
		}

		return false;
	}

	private static bool StartsWithAny(string text, IEnumerable<string> prefixes)
	{
		foreach (var prefix in prefixes)
		{
			if (text.StartsWith(prefix, StringComparison.Ordinal))
			{
				return true;
			}
		}

		return false;
	}

	private static SuggestIdeasRequest NormalizeSuggestIdeasRequest(SuggestIdeasRequest? request)
	{
		return new SuggestIdeasRequest
		{
			UseInference = request?.UseInference ?? true,
			ProviderId = request?.ProviderId,
			ModelId = string.IsNullOrWhiteSpace(request?.ModelId) ? null : request.ModelId.Trim(),
			IdeaCount = Math.Clamp(request?.IdeaCount ?? SuggestIdeasRequest.DefaultIdeaCount, SuggestIdeasRequest.MinIdeaCount, SuggestIdeasRequest.MaxIdeaCount),
			AdditionalContext = string.IsNullOrWhiteSpace(request?.AdditionalContext) ? null : request.AdditionalContext.Trim()
		};
	}

	private static InferenceModel? ResolveSuggestionModel(InferenceProvider provider)
	{
		return provider.Models
			.Where(model => model.IsAvailable && model.IsDefault && string.Equals(model.TaskType, "suggest", StringComparison.OrdinalIgnoreCase))
			.OrderBy(model => model.ModelId)
			.FirstOrDefault()
			?? provider.Models
				.Where(model => model.IsAvailable && model.IsDefault && string.Equals(model.TaskType, "default", StringComparison.OrdinalIgnoreCase))
				.OrderBy(model => model.ModelId)
				.FirstOrDefault();
	}

	private sealed record SuggestionGenerationResult(
		SuggestIdeasResult Result,
		string? ResponseText,
		string? ModelUsed,
		long? DurationMs)
	{
		public static SuggestionGenerationResult Success(string responseText, string? modelUsed, long? durationMs)
			=> new(
				new SuggestIdeasResult
				{
					Success = true,
					Stage = SuggestIdeasStage.Success
				},
				responseText,
				modelUsed,
				durationMs);

		public static SuggestionGenerationResult Fail(SuggestIdeasResult result)
			=> new(result, null, result.ModelUsed, result.InferenceDurationMs);
	}
}
