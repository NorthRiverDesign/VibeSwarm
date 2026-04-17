using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using VibeSwarm.Client.Components.Projects;
using VibeSwarm.Client.Models;
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
	services.AddSingleton<IAgentService>(new FakeAgentService([]));
services.AddSingleton<ISettingsService>(new FakeSettingsService());
services.AddSingleton<IInferenceProviderService>(new FakeInferenceProviderService([]));
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
Assert.Contains("Agents", html);
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
public void AddAgent_AssignmentSeedsDefaultProviderAndModel()
{
	var provider = new Provider
	{
		Id = Guid.NewGuid(),
		Name = "GitHub Copilot",
		Type = ProviderType.Copilot,
		IsEnabled = true
	};
	var agent = new Agent
	{
		Id = Guid.NewGuid(),
		Name = "Backend Engineer",
		DefaultProviderId = provider.Id,
		DefaultProvider = provider,
		DefaultModelId = "gpt-5.4",
		IsEnabled = true
	};

	using var context = CreateBunitContext(agents: [agent], provider: provider);

	var cut = context.Render<ProjectModal>(parameters => parameters
		.Add(component => component.IsVisible, true));

	cut.FindAll("button")
		.Single(button => button.TextContent.Contains("Backend Engineer", StringComparison.Ordinal))
		.Click();

	Assert.Equal(provider.Id.ToString(), cut.Find($"#team-provider-{agent.Id}").GetAttribute("value"));
	Assert.Equal("gpt-5.4", cut.Find($"#team-model-{agent.Id}").GetAttribute("value"));
}

	[Fact]
	public void EditProject_LoadsCommitSummaryInferenceSettings()
	{
	var inferenceProviderId = Guid.NewGuid();
	var inferenceProvider = new InferenceProvider
	{
		Id = inferenceProviderId,
		Name = "Local Ollama",
		ProviderType = VibeSwarm.Shared.Inference.InferenceProviderType.Ollama,
		Endpoint = "http://localhost:11434",
		IsEnabled = true,
		Models =
		[
			new InferenceModel
			{
				InferenceProviderId = inferenceProviderId,
				ModelId = "qwen3",
				DisplayName = "Qwen 3",
				IsAvailable = true,
				IsDefault = true
			}
		]
	};

	using var context = CreateBunitContext(
		inferenceProviders: [inferenceProvider]);

	var cut = context.Render<ProjectModal>(parameters => parameters
		.Add(component => component.IsVisible, true)
		.Add(component => component.EditProject, new Project
		{
			Id = Guid.NewGuid(),
			Name = "Sample Project",
			WorkingPath = "/tmp/sample",
			CommitSummaryInferenceProviderId = inferenceProviderId,
			CommitSummaryInferenceModelId = "qwen3"
		}));

	Assert.Contains("Commit Summary Source", cut.Markup);
	Assert.Equal(inferenceProviderId.ToString(), cut.Find("#modal-commitSummaryInferenceProvider").GetAttribute("value"));
	Assert.Equal("qwen3", cut.Find("#modal-commitSummaryInferenceModel").GetAttribute("value"));
	}

	[Fact]
	public void EditProject_ShowsSinglePlanningReasoningInput()
	{
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "GitHub Copilot",
			Type = ProviderType.Copilot,
			IsEnabled = true
		};

		using var context = CreateBunitContext(provider: provider);

		var cut = context.Render<ProjectModal>(parameters => parameters
			.Add(component => component.IsVisible, true)
			.Add(component => component.EditProject, new Project
			{
				Id = Guid.NewGuid(),
				Name = "Sample Project",
				WorkingPath = "/tmp/sample",
				PlanningEnabled = true,
				PlanningProviderId = provider.Id,
				PlanningReasoningEffort = "medium"
			}));

		Assert.Single(cut.FindAll("#modal-planningReasoning"));
		Assert.Single(cut.FindAll("label[for='modal-planningReasoning']"));
	}

	[Fact]
	public void EditProject_ClearProjectMemoryConfirmationClearsMemoryField()
	{
		using var context = CreateBunitContext();

		var cut = context.Render<ProjectModal>(parameters => parameters
			.Add(component => component.IsVisible, true)
			.Add(component => component.EditProject, new Project
			{
				Id = Guid.NewGuid(),
				Name = "Sample Project",
				WorkingPath = "/tmp/sample",
				Memory = "Remember the deployment checklist."
			}));

		cut.FindAll("button")
			.Single(button => button.TextContent.Contains("Clear", StringComparison.Ordinal))
			.Click();

		Assert.Contains("Clear project memory", cut.Markup, StringComparison.OrdinalIgnoreCase);

		cut.FindAll("button")
			.Single(button => button.TextContent.Contains("Clear memory", StringComparison.OrdinalIgnoreCase))
			.Click();

		Assert.True(string.IsNullOrEmpty(cut.Find("#modal-projectMemory").GetAttribute("value")));
		Assert.DoesNotContain(">Clear<", cut.Markup, StringComparison.Ordinal);
	}

private static BunitContext CreateBunitContext(
	FakeProjectService? projectService = null,
	IReadOnlyList<Agent>? agents = null,
	Provider? provider = null,
	IReadOnlyList<InferenceProvider>? inferenceProviders = null)
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
	context.Services.AddSingleton<IAgentService>(new FakeAgentService(agents ?? []));
	context.Services.AddSingleton<ISettingsService>(new FakeSettingsService());
	context.Services.AddSingleton<IInferenceProviderService>(new FakeInferenceProviderService(inferenceProviders ?? []));
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

private sealed class FakeAgentService(IReadOnlyList<Agent> agents) : IAgentService
{
	private readonly IReadOnlyList<Agent> _agents = agents;

	public Task<IEnumerable<Agent>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Agent>>(_agents);
	public Task<IEnumerable<Agent>> GetEnabledAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Agent>>(_agents.Where(agent => agent.IsEnabled));
	public Task<Agent?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(_agents.FirstOrDefault(agent => agent.Id == id));
	public Task<Agent> CreateAsync(Agent agent, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public Task<Agent> UpdateAsync(Agent agent, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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

private sealed class FakeInferenceProviderService(IReadOnlyList<InferenceProvider> providers) : IInferenceProviderService
{
private readonly IReadOnlyList<InferenceProvider> _providers = providers;
public Task<IEnumerable<InferenceProvider>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IEnumerable<InferenceProvider>>(_providers);
public Task<InferenceProvider?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult(_providers.FirstOrDefault(provider => provider.Id == id));
public Task<IEnumerable<InferenceProvider>> GetEnabledAsync(CancellationToken ct = default) => Task.FromResult<IEnumerable<InferenceProvider>>(_providers.Where(provider => provider.IsEnabled));
public Task<InferenceProvider> CreateAsync(InferenceProvider provider, CancellationToken ct = default) => throw new NotSupportedException();
public Task<InferenceProvider> UpdateAsync(InferenceProvider provider, CancellationToken ct = default) => throw new NotSupportedException();
public Task DeleteAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
public Task<IEnumerable<InferenceModel>> GetModelsAsync(Guid providerId, CancellationToken ct = default) => Task.FromResult<IEnumerable<InferenceModel>>(_providers.FirstOrDefault(provider => provider.Id == providerId)?.Models ?? []);
public Task<IEnumerable<InferenceModel>> RefreshModelsAsync(Guid providerId, CancellationToken ct = default) => Task.FromResult<IEnumerable<InferenceModel>>([]);
public Task SetModelForTaskAsync(Guid providerId, string modelId, string taskType, CancellationToken ct = default) => throw new NotSupportedException();
public Task<InferenceModel?> GetModelForTaskAsync(string taskType, CancellationToken ct = default) => Task.FromResult<InferenceModel?>(null);
}
}
