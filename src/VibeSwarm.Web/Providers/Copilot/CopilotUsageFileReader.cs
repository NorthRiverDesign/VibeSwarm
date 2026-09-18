using System.Text.Json;

namespace VibeSwarm.Shared.Providers.Copilot;

/// <summary>
/// Reads the JSON usage report Copilot CLI writes when given <c>--usage-output-file</c>.
/// </summary>
/// <remarks>
/// GitHub does not publish a schema for this file. The field names below were captured from
/// a real report produced by Copilot CLI 1.0.86:
/// <code>
/// {
///   "totalPremiumRequestCost": 1,
///   "totalUserRequests": 1,
///   "totalNanoAiu": 1928450000,
///   "tokenDetails": {
///     "input":       { "tokenCount": 2 },
///     "cache_read":  { "tokenCount": 14465 },
///     "cache_write": { "tokenCount": 6539 },
///     "output":      { "tokenCount": 4 }
///   },
///   "modelMetrics": { "claude-sonnet-5": { "requests": { "count": 1, "cost": 1 }, ... } },
///   "codeChanges": { "linesAdded": 0, "linesRemoved": 0, "filesModifiedCount": 0 }
/// }
/// </code>
/// The report describes what a session <em>consumed</em>; it carries no quota or budget, so
/// it cannot say how close the account is to a limit. Remaining budget still comes from the
/// provider's configured limit.
///
/// "AIU" is GitHub's AI Unit, reported in billionths — the figure the CLI shows as
/// "AI Credits". Every field is optional: a missing or unrecognised report leaves the
/// caller's stderr-derived values untouched rather than failing a job.
/// </remarks>
public static class CopilotUsageFileReader
{
	/// <summary>
	/// Reads a usage report and applies what it contains to <paramref name="result"/>.
	/// </summary>
	/// <returns>True when at least one field was read and applied.</returns>
	public static bool TryApply(string? usageFilePath, ExecutionResult result)
	{
		var usage = Read(usageFilePath);
		if (usage == null)
		{
			return false;
		}

		var applied = false;

		if (usage.InputTokens.HasValue)
		{
			result.InputTokens = usage.InputTokens;
			result.IsTokenEstimate = false;
			applied = true;
		}

		if (usage.OutputTokens.HasValue)
		{
			result.OutputTokens = usage.OutputTokens;
			result.IsTokenEstimate = false;
			applied = true;
		}

		if (usage.PremiumRequests.HasValue)
		{
			result.PremiumRequestsConsumed = usage.PremiumRequests;
			applied = true;
		}

		if (usage.AiCreditsUsed.HasValue)
		{
			result.AiCreditsConsumed = usage.AiCreditsUsed;
			applied = true;
		}

		if (!string.IsNullOrWhiteSpace(usage.Model))
		{
			result.ModelUsed = usage.Model;
			applied = true;
		}

		return applied;
	}

	/// <summary>
	/// Parses a usage report, returning null when it is missing, empty or unreadable.
	/// </summary>
	public static CopilotUsageReport? Read(string? usageFilePath)
	{
		if (string.IsNullOrWhiteSpace(usageFilePath) || !File.Exists(usageFilePath))
		{
			return null;
		}

		try
		{
			var json = File.ReadAllText(usageFilePath);
			if (string.IsNullOrWhiteSpace(json))
			{
				return null;
			}

			using var document = JsonDocument.Parse(json);
			var root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object)
			{
				return null;
			}

			var report = new CopilotUsageReport
			{
				PremiumRequests = ReadInt(root, "totalPremiumRequestCost"),
				UserRequests = ReadInt(root, "totalUserRequests"),
				AiCreditsUsed = ReadNanoAiu(root, "totalNanoAiu"),
				Model = ReadDominantModel(root),
			};

			ReadTokenDetails(root, report);

			return report.HasAnyValue ? report : null;
		}
		catch (JsonException)
		{
			return null;
		}
		catch (IOException)
		{
			return null;
		}
		catch (UnauthorizedAccessException)
		{
			return null;
		}
	}

	/// <summary>
	/// Sums the token buckets. Copilot reports uncached input, cache reads and cache writes
	/// separately; all three are input tokens, and their total is what the per-model block
	/// reports as <c>inputTokens</c>.
	/// </summary>
	private static void ReadTokenDetails(JsonElement root, CopilotUsageReport report)
	{
		if (!root.TryGetProperty("tokenDetails", out var tokenDetails)
			|| tokenDetails.ValueKind != JsonValueKind.Object)
		{
			return;
		}

		var uncachedInput = ReadTokenCount(tokenDetails, "input");
		report.CacheReadTokens = ReadTokenCount(tokenDetails, "cache_read");
		report.CacheWriteTokens = ReadTokenCount(tokenDetails, "cache_write");
		report.OutputTokens = ReadTokenCount(tokenDetails, "output");

		if (uncachedInput.HasValue || report.CacheReadTokens.HasValue || report.CacheWriteTokens.HasValue)
		{
			report.InputTokens = (uncachedInput ?? 0)
				+ (report.CacheReadTokens ?? 0)
				+ (report.CacheWriteTokens ?? 0);
		}
	}

	private static int? ReadTokenCount(JsonElement tokenDetails, string bucket)
		=> tokenDetails.TryGetProperty(bucket, out var element) && element.ValueKind == JsonValueKind.Object
			? ReadInt(element, "tokenCount")
			: null;

	/// <summary>
	/// Returns the model that served the most requests. A session can switch models, and the
	/// dominant one is the most useful single label for the run.
	/// </summary>
	private static string? ReadDominantModel(JsonElement root)
	{
		if (!root.TryGetProperty("modelMetrics", out var modelMetrics)
			|| modelMetrics.ValueKind != JsonValueKind.Object)
		{
			return null;
		}

		string? dominantModel = null;
		var highestRequestCount = -1;

		foreach (var model in modelMetrics.EnumerateObject())
		{
			var requestCount = model.Value.ValueKind == JsonValueKind.Object
				&& model.Value.TryGetProperty("requests", out var requests)
				&& requests.ValueKind == JsonValueKind.Object
					? ReadInt(requests, "count") ?? 0
					: 0;

			if (requestCount > highestRequestCount)
			{
				highestRequestCount = requestCount;
				dominantModel = model.Name;
			}
		}

		return dominantModel;
	}

	/// <summary>
	/// Converts GitHub's nano-AIU figure to whole AI Units.
	/// </summary>
	private static decimal? ReadNanoAiu(JsonElement element, string propertyName)
	{
		if (!element.TryGetProperty(propertyName, out var value)
			|| value.ValueKind != JsonValueKind.Number
			|| !value.TryGetDouble(out var nanoAiu))
		{
			return null;
		}

		return Math.Round((decimal)(nanoAiu / 1_000_000_000d), 6);
	}

	private static int? ReadInt(JsonElement element, string propertyName)
		=> element.TryGetProperty(propertyName, out var value)
			&& value.ValueKind == JsonValueKind.Number
			&& value.TryGetDouble(out var number)
				? (int)Math.Round(number)
				: null;
}

/// <summary>
/// What a Copilot session consumed, as reported by <c>--usage-output-file</c>.
/// </summary>
public sealed class CopilotUsageReport
{
	/// <summary>Premium requests billed for the session (<c>totalPremiumRequestCost</c>).</summary>
	public int? PremiumRequests { get; set; }

	/// <summary>Prompts the user submitted (<c>totalUserRequests</c>).</summary>
	public int? UserRequests { get; set; }

	/// <summary>GitHub AI Units consumed, converted from <c>totalNanoAiu</c>.</summary>
	public decimal? AiCreditsUsed { get; set; }

	/// <summary>Uncached input plus cache reads and writes.</summary>
	public int? InputTokens { get; set; }

	public int? OutputTokens { get; set; }
	public int? CacheReadTokens { get; set; }
	public int? CacheWriteTokens { get; set; }

	/// <summary>The model that served the most requests in the session.</summary>
	public string? Model { get; set; }

	public bool HasAnyValue
		=> PremiumRequests.HasValue
			|| UserRequests.HasValue
			|| AiCreditsUsed.HasValue
			|| InputTokens.HasValue
			|| OutputTokens.HasValue
			|| Model != null;
}
