using System.Text.Json;

namespace VibeSwarm.Shared.Providers.Copilot;

/// <summary>
/// Reads the JSON usage report that Copilot CLI writes when given <c>--usage-output-file</c>.
/// </summary>
/// <remarks>
/// GitHub does not publish a schema for this file, and the billing model is mid-migration from
/// premium requests to AI Credits, so the reader walks the document and matches on key names
/// instead of binding to a fixed shape. Keys are normalised (case and punctuation stripped)
/// before comparison, so <c>premium_requests</c>, <c>premiumRequests</c> and
/// <c>PremiumRequests</c> all match.
///
/// Anything not recognised is ignored and the caller falls back to parsing stderr, so a schema
/// change degrades to the previous behaviour rather than failing a job.
/// Verified to exist on Copilot CLI 1.0.86; field names are best-effort.
/// </remarks>
public static class CopilotUsageFileReader
{
	private static readonly string[] PremiumRequestKeys =
	[
		"premiumrequests", "premiumrequestsused", "premiumrequestcount",
		"premiumrequestsconsumed", "totalpremiumrequests"
	];

	private static readonly string[] PremiumRequestLimitKeys =
	[
		"premiumrequestslimit", "premiumrequestlimit", "premiumrequestsquota",
		"premiumrequestsallowed", "premiumrequestsmax"
	];

	private static readonly string[] CreditKeys =
	[
		"aicredits", "aicreditsused", "creditsused", "credits", "creditsconsumed",
		"totalcredits", "githubaicredits"
	];

	private static readonly string[] CreditLimitKeys =
	[
		"aicreditslimit", "creditslimit", "maxaicredits", "maxcredits",
		"creditquota", "creditsquota", "creditsallowed"
	];

	private static readonly string[] InputTokenKeys =
	[
		"inputtokens", "prompttokens", "totalinputtokens", "totalprompttokens"
	];

	private static readonly string[] OutputTokenKeys =
	[
		"outputtokens", "completiontokens", "totaloutputtokens", "totalcompletiontokens"
	];

	private static readonly string[] CostKeys =
	[
		"costusd", "totalcostusd", "totalcost", "estimatedcost", "estimatedcostusd", "cost"
	];

	private static readonly string[] ModelKeys = ["model", "modelid", "modelname"];

	/// <summary>
	/// Reads a usage report and applies whatever it recognises to <paramref name="result"/>.
	/// </summary>
	/// <returns>True when at least one field was recognised and applied.</returns>
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

		if (usage.CostUsd.HasValue)
		{
			result.CostUsd = usage.CostUsd;
			applied = true;
		}

		if (usage.PremiumRequests.HasValue)
		{
			result.PremiumRequestsConsumed = usage.PremiumRequests;
			applied = true;
		}

		if (!string.IsNullOrWhiteSpace(usage.Model))
		{
			result.ModelUsed = usage.Model;
			applied = true;
		}

		var limits = usage.ToUsageLimits();
		if (limits != null)
		{
			result.DetectedUsageLimits = result.DetectedUsageLimits == null
				? limits
				: UsageLimitWindowHelper.Merge(result.DetectedUsageLimits, limits);
			applied = true;
		}

		return applied;
	}

	/// <summary>
	/// Parses a usage report, returning null when the file is missing, empty, or unreadable.
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
			var report = new CopilotUsageReport();
			Walk(document.RootElement, report);
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

	private static void Walk(JsonElement element, CopilotUsageReport report)
	{
		switch (element.ValueKind)
		{
			case JsonValueKind.Object:
				foreach (var property in element.EnumerateObject())
				{
					Capture(property.Name, property.Value, report);
					Walk(property.Value, report);
				}
				break;

			case JsonValueKind.Array:
				foreach (var item in element.EnumerateArray())
				{
					Walk(item, report);
				}
				break;
		}
	}

	private static void Capture(string name, JsonElement value, CopilotUsageReport report)
	{
		var key = Normalize(name);

		if (value.ValueKind == JsonValueKind.String)
		{
			if (Matches(key, ModelKeys) && report.Model == null)
			{
				var text = value.GetString();
				if (!string.IsNullOrWhiteSpace(text))
				{
					report.Model = text;
				}
			}
			return;
		}

		if (value.ValueKind != JsonValueKind.Number)
		{
			return;
		}

		if (Matches(key, PremiumRequestLimitKeys)) report.PremiumRequestLimit ??= ToInt(value);
		else if (Matches(key, PremiumRequestKeys)) report.PremiumRequests ??= ToInt(value);
		else if (Matches(key, CreditLimitKeys)) report.CreditLimit ??= ToDecimal(value);
		else if (Matches(key, CreditKeys)) report.CreditsUsed ??= ToDecimal(value);
		else if (Matches(key, InputTokenKeys)) report.InputTokens ??= ToInt(value);
		else if (Matches(key, OutputTokenKeys)) report.OutputTokens ??= ToInt(value);
		else if (Matches(key, CostKeys)) report.CostUsd ??= ToDecimal(value);
	}

	/// <summary>
	/// Strips case and punctuation so differing naming conventions compare equal.
	/// </summary>
	private static string Normalize(string name)
		=> string.Concat(name.Where(char.IsLetterOrDigit)).ToLowerInvariant();

	private static bool Matches(string normalizedKey, string[] candidates)
		=> Array.IndexOf(candidates, normalizedKey) >= 0;

	private static int? ToInt(JsonElement value)
		=> value.TryGetInt32(out var parsed) ? parsed : null;

	private static decimal? ToDecimal(JsonElement value)
		=> value.TryGetDecimal(out var parsed) ? parsed : null;
}

/// <summary>
/// The fields recognised in a Copilot usage report.
/// </summary>
public sealed class CopilotUsageReport
{
	public int? PremiumRequests { get; set; }
	public int? PremiumRequestLimit { get; set; }
	public decimal? CreditsUsed { get; set; }
	public decimal? CreditLimit { get; set; }
	public int? InputTokens { get; set; }
	public int? OutputTokens { get; set; }
	public decimal? CostUsd { get; set; }
	public string? Model { get; set; }

	public bool HasAnyValue
		=> PremiumRequests.HasValue
			|| PremiumRequestLimit.HasValue
			|| CreditsUsed.HasValue
			|| CreditLimit.HasValue
			|| InputTokens.HasValue
			|| OutputTokens.HasValue
			|| CostUsd.HasValue
			|| Model != null;

	/// <summary>
	/// Builds monthly usage windows from whichever budget the report describes.
	/// Copilot meters both premium requests and AI Credits on a monthly cycle.
	/// </summary>
	public UsageLimits? ToUsageLimits()
	{
		var windows = new List<UsageLimitWindow>();

		if (PremiumRequests.HasValue || PremiumRequestLimit.HasValue)
		{
			windows.Add(new UsageLimitWindow
			{
				Scope = UsageLimitWindowScope.Monthly,
				LimitType = UsageLimitType.PremiumRequests,
				CurrentUsage = PremiumRequests,
				MaxUsage = PremiumRequestLimit,
				IsLimitReached = PremiumRequests.HasValue
					&& PremiumRequestLimit is > 0
					&& PremiumRequests >= PremiumRequestLimit
			});
		}

		if (CreditsUsed.HasValue || CreditLimit.HasValue)
		{
			windows.Add(new UsageLimitWindow
			{
				Scope = UsageLimitWindowScope.Monthly,
				LimitType = UsageLimitType.AiCredits,
				CurrentUsage = CreditsUsed.HasValue ? (int)Math.Round(CreditsUsed.Value) : null,
				MaxUsage = CreditLimit.HasValue ? (int)Math.Round(CreditLimit.Value) : null,
				IsLimitReached = CreditsUsed.HasValue
					&& CreditLimit is > 0
					&& CreditsUsed >= CreditLimit
			});
		}

		if (windows.Count == 0)
		{
			return null;
		}

		return UsageLimitWindowHelper.CreateUsageLimits(
			windows[0].LimitType,
			message: null,
			windows);
	}
}
