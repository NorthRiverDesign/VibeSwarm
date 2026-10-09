using System.Text.Json;
using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Services;

/// <summary>
/// "Set up local environment": a job that gets a cloned project running on this machine (local
/// config, database, mail trap, dependencies) rather than changing its code. The agent reports
/// where the app runs in a small result file, which VibeSwarm turns into the project's Local
/// environment so later jobs can start and test it.
/// </summary>
public static class LocalEnvironmentSetup
{
	public const string JobTag = "local-environment-setup";
	public const string JobTitle = "Set up local environment";
	public const string EnvironmentName = "Local";

	/// <summary>Written by the agent under the working directory's .vibeswarm/, which git ignores.</summary>
	public const string ResultFileName = "local-environment.json";

	public const string GoalPrompt =
		"Set up this project for local development on this machine so it builds and runs here. " +
		"Install its dependencies, create the local configuration it is missing (such as a .env file), " +
		"provision a local database and email trapping when the project needs them, run migrations and seed data, " +
		"then start the app and confirm it responds.";

	private const int MaxDescriptionLength = 1000;
	private const int MaxUsernameLength = 200;
	private const int MaxPasswordLength = 500;

	private static readonly JsonSerializerOptions ResultJsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true
	};

	public static Job CreateJob(Guid projectId, Guid providerId, string? modelId, string? branch) => new()
	{
		ProjectId = projectId,
		ProviderId = providerId,
		ModelUsed = string.IsNullOrWhiteSpace(modelId) ? null : modelId,
		Branch = string.IsNullOrWhiteSpace(branch) ? null : branch,
		Title = JobTitle,
		GoalPrompt = GoalPrompt,
		Tags = JobTag
	};

	public static bool IsSetupJob(Job? job) =>
		!string.IsNullOrWhiteSpace(job?.Tags) &&
		job.Tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
			.Contains(JobTag, StringComparer.OrdinalIgnoreCase);

	/// <summary>Reads the agent's result file; null when it is empty or not valid JSON.</summary>
	public static LocalEnvironmentSetupResult? ParseResult(string? json)
	{
		if (string.IsNullOrWhiteSpace(json))
		{
			return null;
		}

		try
		{
			return JsonSerializer.Deserialize<LocalEnvironmentSetupResult>(json, ResultJsonOptions);
		}
		catch (JsonException)
		{
			return null;
		}
	}

	/// <summary>
	/// Records the result as the project's Local environment, updating the existing one when there
	/// is one. Returns null, changing nothing, when the result has no usable http(s) URL.
	/// </summary>
	public static ProjectEnvironment? ApplyResult(Project project, LocalEnvironmentSetupResult result)
	{
		var url = result.Url?.Trim();
		if (string.IsNullOrEmpty(url) ||
			url.Length > 1000 ||
			!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
			(uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
		{
			return null;
		}

		project.Environments ??= [];
		var now = DateTime.UtcNow;
		// Names are unique per project, so an environment already called Local is reused too.
		var environment = project.Environments
			.Where(item => item.Stage == EnvironmentStage.Local ||
				string.Equals(item.Name, EnvironmentName, StringComparison.OrdinalIgnoreCase))
			.OrderByDescending(item => item.Stage == EnvironmentStage.Local)
			.ThenByDescending(item => string.Equals(item.Name, EnvironmentName, StringComparison.OrdinalIgnoreCase))
			.ThenBy(item => item.SortOrder)
			.FirstOrDefault();

		if (environment == null)
		{
			environment = new ProjectEnvironment
			{
				Id = Guid.NewGuid(),
				ProjectId = project.Id,
				Name = EnvironmentName,
				IsPrimary = !project.Environments.Any(item => item.IsEnabled),
				SortOrder = project.Environments.Count == 0 ? 0 : project.Environments.Max(item => item.SortOrder) + 1,
				CreatedAt = now
			};
			project.Environments.Add(environment);
		}

		environment.Url = url;
		environment.Type = EnvironmentType.Web;
		environment.Stage = EnvironmentStage.Local;
		environment.IsEnabled = true;
		environment.Description = BuildDescription(result) ?? environment.Description;
		environment.UpdatedAt = now;

		var username = result.Username?.Trim();
		if (!string.IsNullOrEmpty(username) || !string.IsNullOrEmpty(result.Password))
		{
			environment.Username = string.IsNullOrEmpty(username) ? null : Truncate(username, MaxUsernameLength);
			environment.Password = string.IsNullOrEmpty(result.Password) ? null : Truncate(result.Password, MaxPasswordLength);
		}

		return environment;
	}

	private static string? BuildDescription(LocalEnvironmentSetupResult result)
	{
		var lines = new List<string>();
		if (!string.IsNullOrWhiteSpace(result.StartCommand))
		{
			lines.Add($"Start: {result.StartCommand.Trim()}");
		}

		if (!string.IsNullOrWhiteSpace(result.Notes))
		{
			lines.Add(result.Notes.Trim());
		}

		return lines.Count == 0 ? null : Truncate(string.Join("\n", lines), MaxDescriptionLength);
	}

	private static string Truncate(string value, int maxLength) =>
		value.Length <= maxLength ? value : value[..maxLength];
}

/// <summary>What a local setup run reports back: where the app answers and how to start it.</summary>
public sealed class LocalEnvironmentSetupResult
{
	public string? Url { get; set; }
	public string? StartCommand { get; set; }
	public string? Notes { get; set; }
	public string? Username { get; set; }
	public string? Password { get; set; }
}
