using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using VibeSwarm.Shared.Data;

namespace VibeSwarm.Web.Services;

public interface IProjectEnvironmentCredentialService
{
void PopulateForClient(Project project);
void PopulateForExecution(Project project);
void Encrypt(ProjectEnvironment environment, ProjectEnvironment? existingEnvironment = null);
}

public class ProjectEnvironmentCredentialService : IProjectEnvironmentCredentialService
{
private const string ProtectorPurpose = "VibeSwarm.ProjectEnvironmentCredentials.v1";
private readonly IDataProtector _protector;

public ProjectEnvironmentCredentialService(IDataProtectionProvider dataProtectionProvider)
{
_protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
}

public void PopulateForClient(Project project)
{
Populate(project, includePassword: false);
}

public void PopulateForExecution(Project project)
{
Populate(project, includePassword: true);
}

public void Encrypt(ProjectEnvironment environment, ProjectEnvironment? existingEnvironment = null)
{
if (environment.Type != EnvironmentType.Web)
{
environment.Username = null;
environment.Password = null;
environment.ClearPassword = false;
environment.EncryptedUsername = null;
environment.EncryptedPassword = null;
environment.HasPassword = false;
return;
}

if (!string.IsNullOrWhiteSpace(environment.Username))
{
environment.EncryptedUsername = _protector.Protect(environment.Username.Trim());
}
else if (!string.IsNullOrWhiteSpace(existingEnvironment?.EncryptedUsername))
{
environment.EncryptedUsername = existingEnvironment.EncryptedUsername;
}
else
{
environment.EncryptedUsername = null;
}

if (environment.ClearPassword)
{
environment.EncryptedPassword = null;
}
else if (!string.IsNullOrWhiteSpace(environment.Password))
{
environment.EncryptedPassword = _protector.Protect(environment.Password);
}
else if (!string.IsNullOrWhiteSpace(existingEnvironment?.EncryptedPassword))
{
environment.EncryptedPassword = existingEnvironment.EncryptedPassword;
}
else
{
environment.EncryptedPassword = null;
}

environment.HasPassword = !string.IsNullOrWhiteSpace(environment.EncryptedPassword);
environment.Password = null;
environment.ClearPassword = false;
}

private void Populate(Project project, bool includePassword)
{
foreach (var environment in project.Environments)
{
if (environment.Type != EnvironmentType.Web)
{
environment.Username = null;
environment.Password = null;
environment.ClearPassword = false;
environment.HasPassword = false;
continue;
}

environment.Username = DecryptOrNull(environment.EncryptedUsername);
environment.Password = includePassword
? DecryptOrNull(environment.EncryptedPassword)
: null;
environment.ClearPassword = false;
environment.HasPassword = !string.IsNullOrWhiteSpace(environment.EncryptedPassword);
}
}

private string? DecryptOrNull(string? protectedValue)
{
if (string.IsNullOrWhiteSpace(protectedValue))
{
return null;
}

try
{
return _protector.Unprotect(protectedValue);
}
catch (CryptographicException ex)
{
throw new InvalidOperationException("Failed to decrypt stored environment credentials. Ensure data protection keys are persisted correctly.", ex);
}
}
}
