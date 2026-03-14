using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using VibeSwarm.Client.Pages;
using VibeSwarm.Client.Services;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.LocalInference;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

public sealed class LocalInferenceAndSettingsPageTests
{
[Fact]
public async Task RenderedLocalInferencePage_ShowsSetupAction_WhenNoProviderConfigured()
{
var services = new ServiceCollection();
services.AddLogging();
services.AddSingleton<IInferenceProviderService>(new FakeInferenceProviderService());
services.AddSingleton<IInferenceService>(new FakeInferenceService());
services.AddSingleton<NotificationService>();

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		var localInferencePageType = typeof(Settings).Assembly.GetType("VibeSwarm.Client.Pages.LocalInference");
		Assert.NotNull(localInferencePageType);

		var html = await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var output = await renderer.RenderComponentAsync(localInferencePageType!, ParameterView.Empty);
			return output.ToHtmlString();
		});

Assert.Contains("Local Inference", html);
Assert.Contains("Set Up Local Inference", html);
Assert.DoesNotContain("App Settings", html);
}

[Fact]
public async Task RenderedSettingsPage_DoesNotShowLocalInferenceTab()
{
var services = new ServiceCollection();
	services.AddLogging();
	services.AddSingleton<ISettingsService>(new FakeSettingsService());
	services.AddSingleton<IFileSystemService>(new FakeFileSystemService());
	services.AddSingleton<IProjectService>(new FakeProjectService());
	services.AddSingleton<NavigationManager>(new TestNavigationManager());
	services.AddSingleton<NotificationService>();
	services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());

await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

var html = await renderer.Dispatcher.InvokeAsync(async () =>
{
var output = await renderer.RenderComponentAsync<Settings>();
return output.ToHtmlString();
});

Assert.Contains("App Settings", html);
Assert.DoesNotContain("Set Up Local Inference", html);
Assert.DoesNotContain("Local Inference", html);
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

private sealed class FakeInferenceService : IInferenceService
{
public Task<InferenceHealthResult> CheckHealthAsync(string? endpoint = null, CancellationToken ct = default) => Task.FromResult(new InferenceHealthResult());
public Task<List<DiscoveredModel>> GetAvailableModelsAsync(string? endpoint = null, CancellationToken ct = default) => Task.FromResult(new List<DiscoveredModel>());
public Task<InferenceResponse> GenerateAsync(InferenceRequest request, CancellationToken ct = default) => throw new NotSupportedException();
public Task<InferenceResponse> GenerateForTaskAsync(string taskType, string prompt, string? systemPrompt = null, CancellationToken ct = default) => throw new NotSupportedException();
}

private sealed class FakeSettingsService : ISettingsService
{
public Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AppSettings());
public Task<AppSettings> UpdateSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.FromResult(settings);
public Task<string?> GetDefaultProjectsDirectoryAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>("/tmp/projects");
}

	private sealed class FakeFileSystemService : IFileSystemService
	{
	public Task<DirectoryListResult> ListDirectoryAsync(string? path, bool directoriesOnly = false)
		=> Task.FromResult(new DirectoryListResult());

	public Task<bool> DirectoryExistsAsync(string path) => Task.FromResult(false);

	public Task<List<DriveEntry>> GetDrivesAsync() => Task.FromResult(new List<DriveEntry>());
	}

	private sealed class FakeProjectService : IProjectService
	{
		public Task<IEnumerable<Project>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Project>>([]);
		public Task<IEnumerable<Project>> GetRecentAsync(int count, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Project>>([]);
		public Task<Project?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Project?>(null);
		public Task<Project?> GetByIdWithJobsAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Project?>(null);
		public Task<Project> CreateAsync(Project project, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Project> UpdateAsync(Project project, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IEnumerable<ProjectWithStats>> GetAllWithStatsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<ProjectWithStats>>([]);
		public Task<IEnumerable<DashboardProjectInfo>> GetRecentWithLatestJobAsync(int count, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<DashboardProjectInfo>>([]);
	}

	private sealed class NoOpJsRuntime : IJSRuntime
	{
	public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
	=> ValueTask.FromResult(default(TValue)!);

	public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
	=> ValueTask.FromResult(default(TValue)!);
	}

	private sealed class TestNavigationManager : NavigationManager
	{
		public TestNavigationManager()
		{
			Initialize("http://localhost/", "http://localhost/settings");
		}

		protected override void NavigateToCore(string uri, bool forceLoad)
		{
			Uri = ToAbsoluteUri(uri).ToString();
		}
	}
}
