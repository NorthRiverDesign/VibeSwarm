using System.IO;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class ProjectEnvironmentFeatureTests : IDisposable
{
private readonly SqliteConnection _connection;
private readonly DbContextOptions<VibeSwarmDbContext> _dbOptions;
private readonly string _dataProtectionDirectory;

public ProjectEnvironmentFeatureTests()
{
_connection = new SqliteConnection("Data Source=:memory:");
_connection.Open();

_dbOptions = new DbContextOptionsBuilder<VibeSwarmDbContext>()
.UseSqlite(_connection)
.Options;

_dataProtectionDirectory = Path.Combine(Path.GetTempPath(), $"vibeswarm-tests-{Guid.NewGuid():N}");
Directory.CreateDirectory(_dataProtectionDirectory);

using var dbContext = CreateDbContext();
dbContext.Database.EnsureCreated();
}

[Fact]
public async Task CreateAsync_EncryptsWebCredentials_AndHydratesClientFields()
{
await using var dbContext = CreateDbContext();
var service = CreateProjectService(dbContext);

var created = await service.CreateAsync(new Project
{
Name = "Environment Project",
WorkingPath = "/tmp/environment-project",
Environments =
[
new ProjectEnvironment
{
Name = "Staging",
Type = EnvironmentType.Web,
Url = "https://staging.example.com",
Username = "tester@example.com",
Password = "Sup3rSecret!"
}
]
});

var stored = await dbContext.ProjectEnvironments.AsNoTracking().SingleAsync();
Assert.NotEqual("tester@example.com", stored.EncryptedUsername);
Assert.NotEqual("Sup3rSecret!", stored.EncryptedPassword);

var environment = Assert.Single(created.Environments);
Assert.Equal("tester@example.com", environment.Username);
Assert.True(environment.HasPassword);
Assert.Null(environment.Password);
}

[Fact]
public async Task UpdateAsync_BlankPasswordPreservesStoredSecret_AndClearPasswordRemovesIt()
{
await using var dbContext = CreateDbContext();
var service = CreateProjectService(dbContext);

var created = await service.CreateAsync(new Project
{
Name = "Environment Project",
WorkingPath = "/tmp/environment-project",
Environments =
[
new ProjectEnvironment
{
Name = "Preview",
Type = EnvironmentType.Web,
Url = "https://preview.example.com",
Username = "preview-user",
Password = "InitialPassword!"
}
]
});

var storedBefore = await dbContext.ProjectEnvironments.AsNoTracking().SingleAsync();
var originalEncryptedPassword = storedBefore.EncryptedPassword;

var environment = Assert.Single(created.Environments);
environment.Password = null;
environment.ClearPassword = false;
created.Environments = [environment];
await service.UpdateAsync(created);

var storedAfterPreserve = await dbContext.ProjectEnvironments.AsNoTracking().SingleAsync();
Assert.Equal(originalEncryptedPassword, storedAfterPreserve.EncryptedPassword);

environment.ClearPassword = true;
created.Environments = [environment];
var updated = await service.UpdateAsync(created);

var storedAfterClear = await dbContext.ProjectEnvironments.AsNoTracking().SingleAsync();
Assert.Null(storedAfterClear.EncryptedPassword);
Assert.False(Assert.Single(updated.Environments).HasPassword);
}

[Fact]
public void BuildStructuredPrompt_IncludesEnvironmentContextAndCredentials()
{
var job = new Job
{
GoalPrompt = "Verify the deployed app works.",
Project = new Project
{
Name = "Web App",
Description = "A deployed app",
Environments =
[
new ProjectEnvironment
{
Name = "Staging",
Type = EnvironmentType.Web,
Url = "https://staging.example.com",
Notes = "Use the seeded admin account.",
IsDefault = true,
IsEnabled = true,
Username = "admin@example.com",
Password = "StagingPassword!"
},
new ProjectEnvironment
{
Name = "Releases",
Type = EnvironmentType.GitHubReleases,
Url = "https://github.com/octo/repo/releases",
IsEnabled = true
}
]
}
};

var prompt = PromptBuilder.BuildStructuredPrompt(job, true);

Assert.Contains("<environments>", prompt);
Assert.Contains("Prefer these URLs instead of assuming localhost.", prompt);
Assert.Contains("https://staging.example.com", prompt);
Assert.Contains("admin@example.com / StagingPassword!", prompt);
Assert.Contains("https://github.com/octo/repo/releases", prompt);
}

[Fact]
public async Task GenerateMcpConfigJsonAsync_AddsPlaywrightServerWhenWebEnvironmentExists()
{
var service = new McpConfigService(new FakeSkillService(
new Skill { Id = Guid.NewGuid(), Name = "repo-map", Content = "Skill content", IsEnabled = true }));

var json = await service.GenerateMcpConfigJsonAsync(new Project
{
Name = "Web App",
WorkingPath = "/tmp/web-app",
Environments =
[
new ProjectEnvironment
{
Name = "Production",
Type = EnvironmentType.Web,
Url = "https://app.example.com",
IsEnabled = true
}
]
});

Assert.Contains("\"playwright\"", json);
Assert.Contains("@playwright/mcp@latest", json);
Assert.Contains("\"repo-map\"", json);
}

private VibeSwarmDbContext CreateDbContext() => new(_dbOptions);

private ProjectService CreateProjectService(VibeSwarmDbContext dbContext)
{
var dataProtectionProvider = DataProtectionProvider.Create(new DirectoryInfo(_dataProtectionDirectory));
var credentialService = new ProjectEnvironmentCredentialService(dataProtectionProvider);
return new ProjectService(dbContext, credentialService);
}

public void Dispose()
{
_connection.Dispose();
if (Directory.Exists(_dataProtectionDirectory))
{
Directory.Delete(_dataProtectionDirectory, recursive: true);
}
}

private sealed class FakeSkillService(params Skill[] skills) : ISkillService
{
private readonly IReadOnlyList<Skill> _skills = skills;

public Task<IEnumerable<Skill>> GetAllAsync(CancellationToken cancellationToken = default)
=> Task.FromResult<IEnumerable<Skill>>(_skills);

public Task<IEnumerable<Skill>> GetEnabledAsync(CancellationToken cancellationToken = default)
=> Task.FromResult<IEnumerable<Skill>>(_skills.Where(skill => skill.IsEnabled));

public Task<Skill?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
=> Task.FromResult(_skills.FirstOrDefault(skill => skill.Id == id));

public Task<Skill?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
=> Task.FromResult(_skills.FirstOrDefault(skill => string.Equals(skill.Name, name, StringComparison.OrdinalIgnoreCase)));

public Task<Skill> CreateAsync(Skill skill, CancellationToken cancellationToken = default) => throw new NotSupportedException();
public Task<Skill> UpdateAsync(Skill skill, CancellationToken cancellationToken = default) => throw new NotSupportedException();
public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
public Task<bool> NameExistsAsync(string name, Guid? excludeId = null, CancellationToken cancellationToken = default)
=> Task.FromResult(_skills.Any(skill => string.Equals(skill.Name, name, StringComparison.OrdinalIgnoreCase) && skill.Id != excludeId));
public Task<string?> ExpandSkillAsync(string description, Guid providerId, string? modelId = null, CancellationToken cancellationToken = default)
=> Task.FromResult<string?>(null);
}
}
