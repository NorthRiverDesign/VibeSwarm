using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using VibeSwarm.Client.Components.Projects;
using VibeSwarm.Client.Services;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.VersionControl.Models;

namespace VibeSwarm.Tests;

public sealed class ProjectModalTests
{
[Fact]
public async Task RenderedProjectModal_UsesProjectBodyClassAndRendersRefactoredSections()
{
var provider = new Provider
{
Id = Guid.NewGuid(),
Name = "GitHub Copilot",
Type = ProviderType.Copilot,
IsEnabled = true
};

var services = new ServiceCollection();
services.AddLogging();
services.AddSingleton<IProjectService>(new FakeProjectService());
services.AddSingleton<IProviderService>(new FakeProviderService(provider));
	services.AddSingleton<ITeamRoleService>(new FakeTeamRoleService([]));
services.AddSingleton<ISettingsService>(new FakeSettingsService());
services.AddSingleton<IInferenceProviderService>(new FakeInferenceProviderService());
services.AddSingleton<NotificationService>();
services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());

await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

var html = await renderer.Dispatcher.InvokeAsync(async () =>
{
var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
{
[nameof(ProjectModal.IsVisible)] = true
});

var output = await renderer.RenderComponentAsync<ProjectModal>(parameters);
return output.ToHtmlString();
});

Assert.Contains("vs-project-modal-body", html);
Assert.Contains("vs-modal-dialog-wide-lg", html);
Assert.Contains("modal-lg", html);
Assert.Contains("Project Details", html);
Assert.Contains("Workspace", html);
Assert.Contains("Project Source", html);
Assert.Contains("Job Behavior", html);
Assert.Contains("Planning", html);
Assert.Contains("Job Execution", html);
Assert.Contains("Instructions &amp; Memory", html);
Assert.Contains("Default Job Model", html);
Assert.Contains("Team Roles", html);
Assert.Contains("Build Verification", html);
Assert.Contains("Create Project", html);
}

[Fact]
public void SubmitCloneModeWithoutOwnerRepository_ShowsValidationMessage()
{
	using var context = CreateBunitContext();

var cut = context.Render<ProjectModal>(parameters => parameters
.Add(component => component.IsVisible, true));

cut.FindAll("button")
.Single(button => button.TextContent.Contains("Clone Existing GitHub Repository", StringComparison.Ordinal))
.Click();
cut.Find("#modal-githubRepo").Input("sample-project");
cut.Find("form").Submit();

	Assert.Contains("GitHub repositories must use the format 'owner/repo'.", cut.Markup);
}

[Fact]
public void BrowseGitHubRepositories_SelectingRepositoryPopulatesCloneInput()
{
	using var context = CreateBunitContext(new FakeProjectService
	{
		RepositoryBrowserResult = new GitHubRepositoryBrowserResult
		{
			IsGitHubCliAvailable = true,
			IsAuthenticated = true,
			Repositories =
			[
				new GitHubRepositoryBrowserItem
				{
					NameWithOwner = "octocat/hello-world",
					Description = "Sample repository"
				}
			]
		}
	});

	var cut = context.Render<ProjectModal>(parameters => parameters
		.Add(component => component.IsVisible, true));

	cut.FindAll("button")
		.Single(button => button.TextContent.Contains("Clone Existing GitHub Repository", StringComparison.Ordinal))
		.Click();
	cut.Find("#modal-githubRepoBrowse").Click();
	cut.FindAll("button")
		.Single(button => button.TextContent.Contains("octocat/hello-world", StringComparison.Ordinal))
		.Click();
	cut.FindAll("button")
		.Single(button => button.TextContent.Contains("Use Repository", StringComparison.Ordinal))
		.Click();

	Assert.Equal("octocat/hello-world", cut.Find("#modal-githubRepo").GetAttribute("value"));
}

[Fact]
public void BrowseGitHubRepositories_NullRepositoryListShowsEmptyState()
{
	using var context = CreateBunitContext(new FakeProjectService
	{
		RepositoryBrowserResult = new GitHubRepositoryBrowserResult
		{
			IsGitHubCliAvailable = true,
			IsAuthenticated = true,
			Repositories = null!
		}
	});

	var cut = context.Render<ProjectModal>(parameters => parameters
		.Add(component => component.IsVisible, true));

	cut.FindAll("button")
		.Single(button => button.TextContent.Contains("Clone Existing GitHub Repository", StringComparison.Ordinal))
		.Click();
	cut.Find("#modal-githubRepoBrowse").Click();

	Assert.Contains("No repositories matched the current filter.", cut.Markup);
}

[Fact]
public void AddTeamRole_AssignmentSeedsDefaultProviderAndModel()
{
	var provider = new Provider
	{
		Id = Guid.NewGuid(),
		Name = "GitHub Copilot",
		Type = ProviderType.Copilot,
		IsEnabled = true
	};
	var teamRole = new TeamRole
	{
		Id = Guid.NewGuid(),
		Name = "Backend Engineer",
		DefaultProviderId = provider.Id,
		DefaultProvider = provider,
		DefaultModelId = "gpt-5.4",
		IsEnabled = true
	};

	using var context = CreateBunitContext(teamRoles: [teamRole], provider: provider);

	var cut = context.Render<ProjectModal>(parameters => parameters
		.Add(component => component.IsVisible, true));

	cut.FindAll("button")
		.Single(button => button.TextContent.Contains("Backend Engineer", StringComparison.Ordinal))
		.Click();

	Assert.Equal(provider.Id.ToString(), cut.Find($"#team-provider-{teamRole.Id}").GetAttribute("value"));
	Assert.Equal("gpt-5.4", cut.Find($"#team-model-{teamRole.Id}").GetAttribute("value"));
}

private static BunitContext CreateBunitContext(
	FakeProjectService? projectService = null,
	IReadOnlyList<TeamRole>? teamRoles = null,
	Provider? provider = null)
{
	var context = new BunitContext();
	context.JSInterop.SetupVoid("eval", "document.body.classList.add('vs-modal-open')");
	context.JSInterop.SetupVoid("eval", "document.body.classList.remove('vs-modal-open')");
	context.JSInterop.SetupVoid("vibeSwarmInitTouchDrag", _ => true);
	context.Services.AddLogging();
	var resolvedProvider = provider ?? new Provider
	{
		Id = Guid.NewGuid(),
		Name = "GitHub Copilot",
		Type = ProviderType.Copilot,
		IsEnabled = true
	};
	context.Services.AddSingleton<IProjectService>(projectService ?? new FakeProjectService());
	context.Services.AddSingleton<IProviderService>(new FakeProviderService(resolvedProvider));
	context.Services.AddSingleton<ITeamRoleService>(new FakeTeamRoleService(teamRoles ?? []));
	context.Services.AddSingleton<ISettingsService>(new FakeSettingsService());
context.Services.AddSingleton<IInferenceProviderService>(new FakeInferenceProviderService());
context.Services.AddSingleton<NotificationService>();
return context;
}

private sealed class FakeProjectService : IProjectService
{
public GitHubRepositoryBrowserResult RepositoryBrowserResult { get; set; } = new();
public Task<IEnumerable<Project>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Project>>([]);
public Task<IEnumerable<Project>> GetRecentAsync(int count, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Project>>([]);
public Task<Project?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Project?>(null);
public Task<Project?> GetByIdWithJobsAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Project?>(null);
public Task<Project> CreateAsync(Project project, CancellationToken cancellationToken = default) => throw new NotSupportedException();
public Task<Project> CreateProjectAsync(ProjectCreationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
public Task<GitHubRepositoryBrowserResult> BrowseGitHubRepositoriesAsync(CancellationToken cancellationToken = default) => Task.FromResult(RepositoryBrowserResult);
public Task<Project> UpdateAsync(Project project, CancellationToken cancellationToken = default) => throw new NotSupportedException();
public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
public Task<IEnumerable<ProjectWithStats>> GetAllWithStatsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<ProjectWithStats>>([]);
public Task<IEnumerable<DashboardProjectInfo>> GetRecentWithLatestJobAsync(int count, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<DashboardProjectInfo>>([]);
public Task<DashboardJobMetrics> GetDashboardJobMetricsAsync(int rangeDays, CancellationToken cancellationToken = default) => Task.FromResult(new DashboardJobMetrics { RangeDays = rangeDays, Buckets = [] });
public Task<IEnumerable<DashboardRunningJobInfo>> GetDashboardRunningJobsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<DashboardRunningJobInfo>>([]);
}

private sealed class FakeProviderService(Provider provider) : IProviderService
{
private readonly Provider _provider = provider;

public Task<IEnumerable<Provider>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Provider>>([_provider]);
public Task<Provider?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Provider?>(id == _provider.Id ? _provider : null);
public Task<Provider?> GetDefaultAsync(CancellationToken cancellationToken = default) => Task.FromResult<Provider?>(_provider);
public IProvider? CreateInstance(Provider config) => null;
public Task<Provider> CreateAsync(Provider provider, CancellationToken cancellationToken = default) => throw new NotSupportedException();
public Task<Provider> UpdateAsync(Provider provider, CancellationToken cancellationToken = default) => throw new NotSupportedException();
public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
public Task<bool> TestConnectionAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
public Task<ConnectionTestResult> TestConnectionWithDetailsAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
public Task SetEnabledAsync(Guid id, bool isEnabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
public Task SetDefaultAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
public Task<SessionSummary> GetSessionSummaryAsync(Guid providerId, string? sessionId, string? workingDirectory = null, string? fallbackOutput = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
public Task<IEnumerable<ProviderModel>> GetModelsAsync(Guid providerId, CancellationToken cancellationToken = default)
=> Task.FromResult<IEnumerable<ProviderModel>>(
[
new ProviderModel
{
ProviderId = providerId,
ModelId = "gpt-5.4",
DisplayName = "GPT-5.4",
IsAvailable = true,
IsDefault = true
}
]);
public Task<IEnumerable<ProviderModel>> RefreshModelsAsync(Guid providerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
public Task SetDefaultModelAsync(Guid providerId, Guid modelId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
public Task<CliUpdateResult> UpdateCliAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

private sealed class FakeSettingsService : ISettingsService
{
public Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AppSettings());
public Task<AppSettings> UpdateSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.FromResult(settings);
public Task<string?> GetDefaultProjectsDirectoryAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>("/tmp/projects");
}

private sealed class FakeTeamRoleService(IReadOnlyList<TeamRole> teamRoles) : ITeamRoleService
{
	private readonly IReadOnlyList<TeamRole> _teamRoles = teamRoles;

	public Task<IEnumerable<TeamRole>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<TeamRole>>(_teamRoles);
	public Task<IEnumerable<TeamRole>> GetEnabledAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<TeamRole>>(_teamRoles.Where(teamRole => teamRole.IsEnabled));
	public Task<TeamRole?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(_teamRoles.FirstOrDefault(teamRole => teamRole.Id == id));
	public Task<TeamRole> CreateAsync(TeamRole teamRole, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public Task<TeamRole> UpdateAsync(TeamRole teamRole, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
public Task<bool> NameExistsAsync(string name, Guid? excludeId = null, CancellationToken cancellationToken = default) => Task.FromResult(false);
}

private sealed class NoOpJsRuntime : IJSRuntime
{
public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
=> ValueTask.FromResult(default(TValue)!);

public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
=> ValueTask.FromResult(default(TValue)!);
}

private sealed class FakeInferenceProviderService : IInferenceProviderService
{
public Task<IEnumerable<InferenceProvider>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IEnumerable<InferenceProvider>>([]);
public Task<InferenceProvider?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<InferenceProvider?>(null);
public Task<IEnumerable<InferenceProvider>> GetEnabledAsync(CancellationToken ct = default) => Task.FromResult<IEnumerable<InferenceProvider>>([]);
public Task<InferenceProvider> CreateAsync(InferenceProvider provider, CancellationToken ct = default) => throw new NotSupportedException();
public Task<InferenceProvider> UpdateAsync(InferenceProvider provider, CancellationToken ct = default) => throw new NotSupportedException();
public Task DeleteAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
public Task<IEnumerable<InferenceModel>> GetModelsAsync(Guid providerId, CancellationToken ct = default) => Task.FromResult<IEnumerable<InferenceModel>>([]);
public Task<IEnumerable<InferenceModel>> RefreshModelsAsync(Guid providerId, CancellationToken ct = default) => Task.FromResult<IEnumerable<InferenceModel>>([]);
public Task SetModelForTaskAsync(Guid providerId, string modelId, string taskType, CancellationToken ct = default) => throw new NotSupportedException();
public Task<InferenceModel?> GetModelForTaskAsync(string taskType, CancellationToken ct = default) => Task.FromResult<InferenceModel?>(null);
}
}
