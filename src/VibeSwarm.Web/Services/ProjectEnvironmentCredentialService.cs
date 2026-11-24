using Microsoft.AspNetCore.DataProtection;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;

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
			if (!ProjectEnvironment.SupportsLogin(environment.Type))
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
			if (!ProjectEnvironment.SupportsLogin(environment.Type))
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
		if (!ProjectEnvironment.SupportsLogin(environment.Type))
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
		return ProjectEnvironmentVariables.Build(project);
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
