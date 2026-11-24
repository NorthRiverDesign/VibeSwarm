using System.Text;
using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Services;

/// <summary>
/// The shell variables a job gets for its project's enabled environments: APP_&lt;NAME&gt;_URL,
/// _USERNAME and _PASSWORD for each, and APP_URL, APP_USERNAME and APP_PASSWORD for the primary.
/// The job prompt names them, so the names and the values both come from here.
/// Logins must already be decrypted onto the environments.
/// </summary>
public static class ProjectEnvironmentVariables
{
	public const string PrimaryUrl = "APP_URL";
	public const string PrimaryUsername = "APP_USERNAME";
	public const string PrimaryPassword = "APP_PASSWORD";

	/// <summary>
	/// Enabled environments, primary first, in the order jobs see them.
	/// </summary>
	public static List<ProjectEnvironment> GetEnabled(IEnumerable<ProjectEnvironment>? environments)
	{
		return (environments ?? [])
			.Where(environment => environment.IsEnabled)
			.OrderByDescending(environment => environment.IsPrimary)
			.ThenBy(environment => environment.SortOrder)
			.ThenBy(environment => environment.Name, StringComparer.OrdinalIgnoreCase)
			.ToList();
	}

	/// <summary>
	/// The variable-name prefix for an environment: "Local Dev" becomes APP_LOCAL_DEV.
	/// </summary>
	public static string GetPrefix(string? environmentName) => $"APP_{SanitizeName(environmentName)}";

	/// <summary>
	/// The named variables this environment sets, in URL, username, password order.
	/// </summary>
	public static List<KeyValuePair<string, string>> GetVariables(ProjectEnvironment environment)
	{
		var prefix = GetPrefix(environment.Name);
		var variables = new List<KeyValuePair<string, string>>(3);
		if (!string.IsNullOrWhiteSpace(environment.Url))
		{
			variables.Add(new($"{prefix}_URL", environment.Url));
		}

		if (!string.IsNullOrWhiteSpace(environment.Username))
		{
			variables.Add(new($"{prefix}_USERNAME", environment.Username));
		}

		if (!string.IsNullOrWhiteSpace(environment.Password))
		{
			variables.Add(new($"{prefix}_PASSWORD", environment.Password));
		}

		return variables;
	}

	/// <summary>
	/// The APP_URL, APP_USERNAME and APP_PASSWORD shortcuts, from the primary (or first) environment.
	/// </summary>
	public static List<KeyValuePair<string, string>> GetPrimaryShortcuts(IReadOnlyList<ProjectEnvironment> enabledEnvironments)
	{
		var shortcuts = new List<KeyValuePair<string, string>>(3);
		if (enabledEnvironments.Count == 0)
		{
			return shortcuts;
		}

		var primary = enabledEnvironments.FirstOrDefault(environment => environment.IsPrimary) ?? enabledEnvironments[0];
		if (!string.IsNullOrWhiteSpace(primary.Url))
		{
			shortcuts.Add(new(PrimaryUrl, primary.Url));
		}

		if (!string.IsNullOrWhiteSpace(primary.Username))
		{
			shortcuts.Add(new(PrimaryUsername, primary.Username));
		}

		if (!string.IsNullOrWhiteSpace(primary.Password))
		{
			shortcuts.Add(new(PrimaryPassword, primary.Password));
		}

		return shortcuts;
	}

	/// <summary>
	/// Every variable for the project's enabled environments, or null when there are none.
	/// </summary>
	public static Dictionary<string, string>? Build(Project? project)
	{
		var enabled = GetEnabled(project?.Environments);
		if (enabled.Count == 0)
		{
			return null;
		}

		var variables = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var environment in enabled)
		{
			foreach (var variable in GetVariables(environment))
			{
				variables[variable.Key] = variable.Value;
			}
		}

		foreach (var shortcut in GetPrimaryShortcuts(enabled))
		{
			variables[shortcut.Key] = shortcut.Value;
		}

		return variables.Count > 0 ? variables : null;
	}

	private static string SanitizeName(string? name)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			return "ENV";
		}

		var sb = new StringBuilder();
		foreach (var c in name.ToUpperInvariant())
		{
			sb.Append(char.IsLetterOrDigit(c) ? c : '_');
		}

		var result = sb.ToString().Trim('_');
		while (result.Contains("__"))
		{
			result = result.Replace("__", "_");
		}

		return string.IsNullOrEmpty(result) ? "ENV" : result;
	}
}
