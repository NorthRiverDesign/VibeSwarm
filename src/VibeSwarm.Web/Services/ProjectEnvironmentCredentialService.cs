using System.Text;
using Microsoft.AspNetCore.DataProtection;
using VibeSwarm.Shared.Data;

namespace VibeSwarm.Web.Services;

public interface IProjectEnvironmentCredentialService
{
	void PrepareForStorage(Project project, IReadOnlyCollection<ProjectEnvironment>? existingEnvironments = null);
	void PopulateForEditing(Project? project);
	void PopulateForExecution(Project? project);
	Dictionary<string, string>? BuildJobEnvironmentVariables(Project? project);
}

public class ProjectEnvironmentCredentialService : IProjectEnvironmentCredentialService
{
	private const string ProtectionPurpose = "VibeSwarm.ProjectEnvironmentCredentials.v1";
	private readonly IDataProtector _protector;

	public ProjectEnvironmentCredentialService(IDataProtectionProvider dataProtectionProvider)
	{
		_protector = dataProtectionProvider.CreateProtector(ProtectionPurpose);
	}

	public void PrepareForStorage(Project project, IReadOnlyCollection<ProjectEnvironment>? existingEnvironments = null)
	{
		foreach (var environment in project.Environments)
		{
			var existingEnvironment = existingEnvironments?.FirstOrDefault(item => item.Id == environment.Id);
			PrepareForStorage(environment, existingEnvironment);
		}
	}

	public void PopulateForEditing(Project? project)
	{
		if (project == null)
		{
			return;
		}

		foreach (var environment in project.Environments)
		{
			if (environment.Type != EnvironmentType.Web)
			{
				environment.Username = null;
				environment.Password = null;
				continue;
			}

			environment.Username = Unprotect(environment.UsernameCiphertext, environment, "username");
			environment.Password = null;
		}
	}

	public void PopulateForExecution(Project? project)
	{
		if (project == null)
		{
			return;
		}

		foreach (var environment in project.Environments)
		{
			if (environment.Type != EnvironmentType.Web)
			{
				environment.Username = null;
				environment.Password = null;
				continue;
			}

			environment.Username = Unprotect(environment.UsernameCiphertext, environment, "username");
			environment.Password = Unprotect(environment.PasswordCiphertext, environment, "password");
		}
	}

	private void PrepareForStorage(ProjectEnvironment environment, ProjectEnvironment? existingEnvironment)
	{
		if (environment.Type != EnvironmentType.Web)
		{
			ClearCredentials(environment);
			return;
		}

		environment.Username = string.IsNullOrWhiteSpace(environment.Username)
			? null
			: environment.Username.Trim();
		environment.Password = string.IsNullOrEmpty(environment.Password)
			? null
			: environment.Password;

		var hasUsername = !string.IsNullOrWhiteSpace(environment.Username);
		var hasPassword = !string.IsNullOrEmpty(environment.Password);

		environment.UsernameCiphertext = hasUsername
			? _protector.Protect(environment.Username!)
			: null;

		if (hasPassword)
		{
			environment.PasswordCiphertext = _protector.Protect(environment.Password!);
			environment.Password = null;
			return;
		}

		if (!environment.ClearPassword && !string.IsNullOrEmpty(existingEnvironment?.PasswordCiphertext))
		{
			environment.PasswordCiphertext = existingEnvironment.PasswordCiphertext;
			return;
		}

		environment.PasswordCiphertext = null;
		environment.Password = null;
	}

	private static void ClearCredentials(ProjectEnvironment environment)
	{
		environment.Username = null;
		environment.Password = null;
		environment.UsernameCiphertext = null;
		environment.PasswordCiphertext = null;
	}

	/// <summary>
	/// Builds environment variables from the project's enabled environments so that CLI providers
	/// can discover and interact with the running app. Must be called after PopulateForExecution
	/// so that credentials are already decrypted on the environment objects.
	/// </summary>
	public Dictionary<string, string>? BuildJobEnvironmentVariables(Project? project)
	{
		if (project == null || project.Environments.Count == 0)
		{
			return null;
		}

		var enabled = project.Environments
			.Where(e => e.IsEnabled)
			.OrderByDescending(e => e.IsPrimary)
			.ThenBy(e => e.SortOrder)
			.ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
			.ToList();

		if (enabled.Count == 0)
		{
			return null;
		}

		var vars = new Dictionary<string, string>(StringComparer.Ordinal);

		foreach (var env in enabled)
		{
			var sanitizedName = SanitizeEnvironmentName(env.Name);

			if (!string.IsNullOrWhiteSpace(env.Url))
			{
				vars[$"APP_{sanitizedName}_URL"] = env.Url;
			}

			if (!string.IsNullOrWhiteSpace(env.Username))
			{
				vars[$"APP_{sanitizedName}_USERNAME"] = env.Username;
			}

			if (!string.IsNullOrWhiteSpace(env.Password))
			{
				vars[$"APP_{sanitizedName}_PASSWORD"] = env.Password;
			}
		}

		// Add shortcut variables for the primary (or first) environment
		var primary = enabled.FirstOrDefault(e => e.IsPrimary) ?? enabled[0];
		if (!string.IsNullOrWhiteSpace(primary.Url))
		{
			vars["APP_URL"] = primary.Url;
		}

		if (!string.IsNullOrWhiteSpace(primary.Username))
		{
			vars["APP_USERNAME"] = primary.Username;
		}

		if (!string.IsNullOrWhiteSpace(primary.Password))
		{
			vars["APP_PASSWORD"] = primary.Password;
		}

		return vars.Count > 0 ? vars : null;
	}

	private static string SanitizeEnvironmentName(string name)
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

	private string? Unprotect(string? ciphertext, ProjectEnvironment environment, string fieldName)
	{
		if (string.IsNullOrEmpty(ciphertext))
		{
			return null;
		}

		try
		{
			return _protector.Unprotect(ciphertext);
		}
		catch (Exception ex)
		{
			throw new InvalidOperationException($"Stored {fieldName} credentials for environment '{environment.Name}' could not be decrypted.", ex);
		}
	}
}
