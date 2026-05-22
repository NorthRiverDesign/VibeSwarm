using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using VibeSwarm.Client.Components.Providers;
using VibeSwarm.Client.Pages;
using VibeSwarm.Client.Services;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Inference;
using VibeSwarm.Shared.Models;
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
	services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		var localInferencePageType = typeof(Settings).Assembly.GetType("VibeSwarm.Client.Pages.LocalInference");
		Assert.NotNull(localInferencePageType);

		var html = await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var output = await renderer.RenderComponentAsync(localInferencePageType!, ParameterView.Empty);
			return output.ToHtmlString();
		});

	Assert.Contains("Inference", html);
	Assert.Contains("Add", html);
	Assert.Contains("Ollama", html);
	Assert.Contains("Grok", html);
	Assert.Contains("No Ollama connections", html);
	Assert.Contains("d-flex align-items-center justify-content-between gap-2 gap-sm-3 mb-3 mb-lg-4", html);
	Assert.DoesNotContain("App Settings", html);
}

[Fact]
public async Task RenderedSettingsPage_DoesNotShowLocalInferenceTab()
{
var services = new ServiceCollection();
	services.AddLogging();
	services.AddSingleton<ISettingsService>(new FakeSettingsService());
	services.AddSingleton<AppTimeZoneService>();
	services.AddSingleton<IDeveloperModeService>(new FakeDeveloperModeService());
	services.AddSingleton<IFileSystemService>(new FakeFileSystemService());
	services.AddSingleton<IProjectService>(new FakeProjectService());
	services.AddSingleton<IDatabaseService>(new FakeDatabaseService());
	services.AddSingleton<ICriticalErrorLogService>(new FakeCriticalErrorLogService());
	services.AddSingleton<NavigationManager>(new TestNavigationManager());
	services.AddSingleton<NotificationService>();
	services.AddSingleton<DeveloperUpdateOverlayService>();
	services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());

await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

var html = await renderer.Dispatcher.InvokeAsync(async () =>
{
var output = await renderer.RenderComponentAsync<Settings>();
return output.ToHtmlString();
});

	Assert.Contains("App Settings", html);
	Assert.Contains("Timezone", html);
	Assert.Contains("Enable provider commit attribution", html);
	Assert.Contains("Idea Prompt Templates", html);
	Assert.Contains("Idea expansion template", html);
	Assert.Contains("Direct idea implementation template", html);
	Assert.Contains("Critical Error Logs", html);
	Assert.Contains("Database", html);
	Assert.Contains("Developer Mode", html);
	Assert.Contains("Rebuild And Restart", html);
	Assert.DoesNotContain("Add Provider", html);
	Assert.DoesNotContain("inference provider", html);
	Assert.DoesNotContain("Database Size", html);
	Assert.DoesNotContain("Open Database Tab", html);
}

[Fact]
public void LocalInferencePage_TestInference_UsesSelectedProviderAndModel()
{
	var firstProviderId = Guid.NewGuid();
	var secondProviderId = Guid.NewGuid();
	var providers = new[]
	{
		new InferenceProvider
		{
			Id = firstProviderId,
			Name = "Local Ollama",
			ProviderType = InferenceProviderType.Ollama,
			Endpoint = "http://ollama:11434",
			IsEnabled = true
		},
		new InferenceProvider
		{
			Id = secondProviderId,
			Name = "Grok Cloud",
			ProviderType = InferenceProviderType.Grok,
			Endpoint = "https://api.x.ai/v1",
			IsEnabled = true
		}
	};
	var modelsByProvider = new Dictionary<Guid, IReadOnlyList<InferenceModel>>
	{
		[firstProviderId] =
		[
			new InferenceModel
			{
				InferenceProviderId = firstProviderId,
				ModelId = "qwen3",
				DisplayName = "Qwen 3",
				IsAvailable = true,
				IsDefault = true,
				TaskType = "default"
			}
		],
		[secondProviderId] =
		[
			new InferenceModel
			{
				InferenceProviderId = secondProviderId,
				ModelId = "grok-beta",
				DisplayName = "Grok Beta",
				IsAvailable = true,
				IsDefault = true,
				TaskType = "default"
			}
		]
	};
	var providerService = new FakeInferenceProviderService(providers, modelsByProvider);
	var inferenceService = new FakeInferenceService
	{
		GenerateResponse = new InferenceResponse
		{
			Success = true,
			Response = "Done",
			ModelUsed = "grok-beta"
		}
	};

	using var context = new BunitContext();
	context.Services.AddLogging();
	context.Services.AddSingleton<IInferenceProviderService>(providerService);
	context.Services.AddSingleton<IInferenceService>(inferenceService);
	context.Services.AddSingleton<NotificationService>();
	context.Services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());

	var cut = context.Render<LocalInference>();

	cut.WaitForAssertion(() =>
	{
		Assert.Contains("Say hello in one sentence.", cut.Markup);
		Assert.DoesNotContain("_promptMessage", cut.Markup);
		Assert.Contains("Qwen 3 (Default)", cut.Markup);
	});

	cut.FindAll("button")
		.Single(button => button.TextContent.Contains("Grok", StringComparison.Ordinal))
		.Click();

	cut.WaitForAssertion(() => Assert.Contains("Grok Beta (Default)", cut.Markup));

	cut.Find($"#testModel-{secondProviderId:D}").Change("grok-beta");
	cut.Find($"#testPrompt-{secondProviderId:D}").Change("Use the selected provider.");
	cut.Find($"#testSubmit-{secondProviderId:D}").Click();

	cut.WaitForAssertion(() => Assert.NotNull(inferenceService.LastRequest));

	Assert.Equal(secondProviderId, inferenceService.LastRequest!.ProviderId);
	Assert.Equal(InferenceProviderType.Grok, inferenceService.LastRequest.ProviderType);
	Assert.Equal("https://api.x.ai/v1", inferenceService.LastRequest.Endpoint);
	Assert.Equal("grok-beta", inferenceService.LastRequest.Model);
	Assert.Equal("Use the selected provider.", inferenceService.LastRequest.Prompt);
}

[Fact]
public void LocalInferencePage_DefaultsToLastUsedProviderFromSessionStorage()
{
	var firstProviderId = Guid.NewGuid();
	var secondProviderId = Guid.NewGuid();
	var providers = new[]
	{
		new InferenceProvider
		{
			Id = firstProviderId,
			Name = "Local Ollama",
			ProviderType = InferenceProviderType.Ollama,
			Endpoint = "http://ollama:11434",
			IsEnabled = true
		},
		new InferenceProvider
		{
			Id = secondProviderId,
			Name = "Grok Cloud",
			ProviderType = InferenceProviderType.Grok,
			Endpoint = "https://api.x.ai/v1",
			IsEnabled = true
		}
	};
	var modelsByProvider = new Dictionary<Guid, IReadOnlyList<InferenceModel>>
	{
		[firstProviderId] =
		[
			new InferenceModel
			{
				InferenceProviderId = firstProviderId,
				ModelId = "qwen3",
				DisplayName = "Qwen 3",
				IsAvailable = true,
				IsDefault = true,
				TaskType = "default"
			}
		],
		[secondProviderId] =
		[
			new InferenceModel
			{
				InferenceProviderId = secondProviderId,
				ModelId = "grok-beta",
				DisplayName = "Grok Beta",
				IsAvailable = true,
				IsDefault = true,
				TaskType = "default"
			}
		]
	};
	var providerService = new FakeInferenceProviderService(providers, modelsByProvider);
	var inferenceService = new FakeInferenceService();
	var jsRuntime = new NoOpJsRuntime(new Dictionary<string, string?>
	{
		["vibeswarm.local-inference.last-test-provider"] = secondProviderId.ToString("D")
	});

	using var context = new BunitContext();
	context.Services.AddLogging();
	context.Services.AddSingleton<IInferenceProviderService>(providerService);
	context.Services.AddSingleton<IInferenceService>(inferenceService);
	context.Services.AddSingleton<NotificationService>();
	context.Services.AddSingleton<IJSRuntime>(jsRuntime);

	var cut = context.Render<LocalInference>();

	cut.WaitForAssertion(() =>
	{
		Assert.Contains("Grok Beta (Default)", cut.Markup);
		Assert.DoesNotContain("Qwen 3 (Default)", cut.Markup);
	});

	cut.Find($"#testSubmit-{secondProviderId:D}").Click();

	cut.WaitForAssertion(() => Assert.NotNull(inferenceService.LastRequest));

	Assert.Equal(secondProviderId, inferenceService.LastRequest!.ProviderId);
}

[Fact]
public void InferenceProvidersSection_ShowsEditDeleteAndRefreshModelsInDropdown()
{
	var provider = CreateInferenceProvider();

	using var context = new BunitContext();
	AddInferenceComponentServices(context, new FakeInferenceProviderService([provider]));

	var cut = context.Render<InferenceProvidersSection>(parameters => parameters
		.Add(component => component.Providers, [provider])
		.Add(component => component.ActiveProviderType, provider.ProviderType));

	Assert.Contains("Inference provider actions", cut.Markup);
	Assert.Contains("dropdown-menu", cut.Markup);
	Assert.Contains("Refresh Models", cut.Markup);
	Assert.Contains("Edit", cut.Markup);
	Assert.Contains("Delete", cut.Markup);
	Assert.DoesNotContain("btn-danger", cut.Markup);
}

[Fact]
public void InferenceProvidersSection_DisablesRefreshModelsActionForRefreshingProvider()
{
	var provider = CreateInferenceProvider();

	using var context = new BunitContext();
	AddInferenceComponentServices(context, new FakeInferenceProviderService([provider]));

	var cut = context.Render<InferenceProvidersSection>(parameters => parameters
		.Add(component => component.Providers, [provider])
		.Add(component => component.ActiveProviderType, provider.ProviderType)
		.Add(component => component.RefreshingModelsProviderId, provider.Id));

	var refreshButton = cut.FindAll("button")
		.Single(button => button.TextContent.Contains("Refresh Models", StringComparison.Ordinal));

	Assert.True(refreshButton.HasAttribute("disabled"));
	Assert.Contains("spin", refreshButton.InnerHtml);
}

[Fact]
public void InferenceProvidersSection_DropdownActionsInvokeProviderCallbacks()
{
	var provider = CreateInferenceProvider();
	InferenceProvider? refreshedProvider = null;
	InferenceProvider? editedProvider = null;
	InferenceProvider? deletedProvider = null;

	using var context = new BunitContext();
	AddInferenceComponentServices(context, new FakeInferenceProviderService([provider]));

	var cut = context.Render<InferenceProvidersSection>(parameters => parameters
		.Add(component => component.Providers, [provider])
		.Add(component => component.ActiveProviderType, provider.ProviderType)
		.Add(component => component.OnRefreshModels, EventCallback.Factory.Create<InferenceProvider>(this, clickedProvider => refreshedProvider = clickedProvider))
		.Add(component => component.OnEditProvider, EventCallback.Factory.Create<InferenceProvider>(this, clickedProvider => editedProvider = clickedProvider))
		.Add(component => component.OnDeleteProvider, EventCallback.Factory.Create<InferenceProvider>(this, clickedProvider => deletedProvider = clickedProvider)));

	cut.FindAll("button")
		.Single(button => button.TextContent.Contains("Refresh Models", StringComparison.Ordinal))
		.Click();
	cut.FindAll("button")
		.Single(button => button.TextContent.Contains("Edit", StringComparison.Ordinal))
		.Click();
	cut.FindAll("button")
		.Single(button => button.TextContent.Contains("Delete", StringComparison.Ordinal))
		.Click();

	Assert.Same(provider, refreshedProvider);
	Assert.Same(provider, editedProvider);
	Assert.Same(provider, deletedProvider);
}

private static void AddInferenceComponentServices(BunitContext context, IInferenceProviderService providerService)
{
	context.Services.AddSingleton(providerService);
	context.Services.AddSingleton<IInferenceService>(new FakeInferenceService());
	context.Services.AddSingleton<NotificationService>();
	context.Services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());
}

[Fact]
public void LocalInferencePage_RefreshModelsDropdownActionRefreshesModelList()
{
	var providerId = Guid.NewGuid();
	var providers = new[]
	{
		CreateInferenceProvider(providerId)
	};
	var initialModels = new Dictionary<Guid, IReadOnlyList<InferenceModel>>
	{
		[providerId] =
		[
			CreateInferenceModel(providerId, "qwen3", "Qwen 3")
		]
	};
	var refreshedModels = new Dictionary<Guid, IReadOnlyList<InferenceModel>>
	{
		[providerId] =
		[
			CreateInferenceModel(providerId, "llama3", "Llama 3")
		]
	};
	var providerService = new FakeInferenceProviderService(providers, initialModels)
	{
		RefreshedModelsByProvider = refreshedModels
	};
	var notificationService = new NotificationService();

	using var context = new BunitContext();
	context.Services.AddLogging();
	context.Services.AddSingleton<IInferenceProviderService>(providerService);
	context.Services.AddSingleton<IInferenceService>(new FakeInferenceService());
	context.Services.AddSingleton(notificationService);
	context.Services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());

	var cut = context.Render<LocalInference>();

	cut.WaitForAssertion(() => Assert.Contains("Qwen 3 (Default)", cut.Markup));

	cut.FindAll("button")
		.Single(button => button.TextContent.Contains("Refresh Models", StringComparison.Ordinal))
		.Click();

	cut.WaitForAssertion(() =>
	{
		Assert.Equal(1, providerService.RefreshModelsCallCount);
		Assert.Equal(providerId, providerService.LastRefreshedProviderId);
		Assert.Contains("Llama 3 (Default)", cut.Markup);
		Assert.DoesNotContain("Qwen 3 (Default)", cut.Markup);
		Assert.Contains(notificationService.NotificationHistory, notification => notification.Message == "Found 1 model(s).");
	});
}

[Fact]
public void LocalInferencePage_RefreshModelsDropdownActionShowsErrorWhenRefreshFails()
{
	var provider = CreateInferenceProvider();
	var providerService = new FakeInferenceProviderService([provider])
	{
		RefreshModelsException = new InvalidOperationException("Provider unavailable")
	};
	var notificationService = new NotificationService();

	using var context = new BunitContext();
	context.Services.AddLogging();
	context.Services.AddSingleton<IInferenceProviderService>(providerService);
	context.Services.AddSingleton<IInferenceService>(new FakeInferenceService());
	context.Services.AddSingleton(notificationService);
	context.Services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());

	var cut = context.Render<LocalInference>();

	cut.WaitForAssertion(() => Assert.Contains("Refresh Models", cut.Markup));

	cut.FindAll("button")
		.Single(button => button.TextContent.Contains("Refresh Models", StringComparison.Ordinal))
		.Click();

	cut.WaitForAssertion(() =>
	{
		Assert.Equal(1, providerService.RefreshModelsCallCount);
		Assert.Contains(notificationService.NotificationHistory, notification => notification.Message == "Failed to refresh models: Provider unavailable");
	});
}

	private sealed class FakeInferenceProviderService : IInferenceProviderService
	{
		private readonly IReadOnlyList<InferenceProvider> _providers;
		private readonly Dictionary<Guid, IReadOnlyList<InferenceModel>> _modelsByProvider;

		public FakeInferenceProviderService(
			IReadOnlyList<InferenceProvider>? providers = null,
			IReadOnlyDictionary<Guid, IReadOnlyList<InferenceModel>>? modelsByProvider = null)
		{
			_providers = providers ?? [];
			_modelsByProvider = modelsByProvider?.ToDictionary() ?? new Dictionary<Guid, IReadOnlyList<InferenceModel>>();
		}

		public IReadOnlyDictionary<Guid, IReadOnlyList<InferenceModel>> RefreshedModelsByProvider { get; init; } = new Dictionary<Guid, IReadOnlyList<InferenceModel>>();
		public Exception? RefreshModelsException { get; init; }
		public int RefreshModelsCallCount { get; private set; }
		public Guid? LastRefreshedProviderId { get; private set; }

	public Task<IEnumerable<InferenceProvider>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IEnumerable<InferenceProvider>>(_providers);
	public Task<InferenceProvider?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult(_providers.FirstOrDefault(provider => provider.Id == id));
	public Task<IEnumerable<InferenceProvider>> GetEnabledAsync(CancellationToken ct = default) => Task.FromResult<IEnumerable<InferenceProvider>>(_providers.Where(provider => provider.IsEnabled).ToList());
	public Task<InferenceProvider> CreateAsync(InferenceProvider provider, CancellationToken ct = default) => throw new NotSupportedException();
	public Task<InferenceProvider> UpdateAsync(InferenceProvider provider, CancellationToken ct = default) => throw new NotSupportedException();
	public Task DeleteAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
	public Task<IEnumerable<InferenceModel>> GetModelsAsync(Guid providerId, CancellationToken ct = default)
		=> Task.FromResult<IEnumerable<InferenceModel>>(_modelsByProvider.TryGetValue(providerId, out var models) ? models : []);
	public Task<IEnumerable<InferenceModel>> RefreshModelsAsync(Guid providerId, CancellationToken ct = default)
	{
		RefreshModelsCallCount++;
		LastRefreshedProviderId = providerId;

		if (RefreshModelsException != null)
		{
			throw RefreshModelsException;
		}

		if (RefreshedModelsByProvider.TryGetValue(providerId, out var refreshedModels))
		{
			_modelsByProvider[providerId] = refreshedModels;
			return Task.FromResult<IEnumerable<InferenceModel>>(refreshedModels);
		}

		return Task.FromResult<IEnumerable<InferenceModel>>(_modelsByProvider.TryGetValue(providerId, out var models) ? models : []);
	}
	public Task SetModelForTaskAsync(Guid providerId, string modelId, string taskType, CancellationToken ct = default) => throw new NotSupportedException();
	public Task<InferenceModel?> GetModelForTaskAsync(string taskType, CancellationToken ct = default) => Task.FromResult<InferenceModel?>(null);
	}

	private static InferenceProvider CreateInferenceProvider(Guid? id = null)
	{
		return new InferenceProvider
		{
			Id = id ?? Guid.NewGuid(),
			Name = "Local Ollama",
			ProviderType = InferenceProviderType.Ollama,
			Endpoint = "http://ollama:11434",
			IsEnabled = true
		};
	}

	private static InferenceModel CreateInferenceModel(Guid providerId, string modelId, string displayName)
	{
		return new InferenceModel
		{
			InferenceProviderId = providerId,
			ModelId = modelId,
			DisplayName = displayName,
			IsAvailable = true,
			IsDefault = true,
			TaskType = "default"
		};
	}

private sealed class FakeInferenceService : IInferenceService
{
public InferenceRequest? LastRequest { get; private set; }
public InferenceResponse GenerateResponse { get; set; } = new() { Success = true, Response = "OK" };
public Task<InferenceHealthResult> CheckHealthAsync(string? endpoint = null, InferenceProviderType? providerType = null, CancellationToken ct = default) => Task.FromResult(new InferenceHealthResult());
public Task<List<DiscoveredModel>> GetAvailableModelsAsync(string? endpoint = null, InferenceProviderType? providerType = null, CancellationToken ct = default) => Task.FromResult(new List<DiscoveredModel>());
public Task<InferenceResponse> GenerateAsync(InferenceRequest request, CancellationToken ct = default)
{
	LastRequest = request;
	return Task.FromResult(GenerateResponse);
}
public Task<InferenceResponse> GenerateForTaskAsync(string taskType, string prompt, string? systemPrompt = null, CancellationToken ct = default) => throw new NotSupportedException();
}

private sealed class FakeSettingsService : ISettingsService
{
public Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AppSettings
{
	TimeZoneId = "UTC",
	CriticalErrorLogRetentionDays = AppSettings.DefaultCriticalErrorLogRetentionDays,
	CriticalErrorLogMaxEntries = AppSettings.DefaultCriticalErrorLogMaxEntries,
	IdeaExpansionPromptTemplate = PromptBuilder.DefaultIdeaExpansionPromptTemplate,
	IdeaImplementationPromptTemplate = PromptBuilder.DefaultIdeaImplementationPromptTemplate,
	ApprovedIdeaImplementationPromptTemplate = PromptBuilder.DefaultApprovedIdeaImplementationPromptTemplate
});
public Task<AppSettings> UpdateSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.FromResult(settings);
public Task<string?> GetDefaultProjectsDirectoryAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>("/tmp/projects");
}

	private sealed class FakeDeveloperModeService : IDeveloperModeService
	{
		public Task<DeveloperModeStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new DeveloperModeStatus
		{
			IsEnabled = true,
			Stage = DeveloperUpdateStage.Ready,
			StatusMessage = "Developer mode is enabled.",
			BuildCommandSummary = "dotnet build VibeSwarm.sln --nologo",
			RestartCommandSummary = "systemctl restart vibeswarm.service",
			WorkingDirectory = "/tmp",
			RecentOutput = []
		});

		public Task<DeveloperModeStatus> StartSelfUpdateAsync(CancellationToken cancellationToken = default) => GetStatusAsync(cancellationToken);
	}

	private sealed class FakeCriticalErrorLogService : ICriticalErrorLogService
	{
		public Task<CriticalErrorLogEntry> LogAsync(CriticalErrorLogEntry entry, CancellationToken cancellationToken = default) => Task.FromResult(entry);
		public Task<IReadOnlyList<CriticalErrorLogEntry>> GetRecentAsync(int limit = 25, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CriticalErrorLogEntry>>([]);
		public Task ApplyRetentionPolicyAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
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
		public Task<Project> CreateProjectAsync(ProjectCreationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Project> UpdateAsync(Project project, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IEnumerable<ProjectWithStats>> GetAllWithStatsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<ProjectWithStats>>([]);
		public Task<IEnumerable<DashboardProjectInfo>> GetRecentWithLatestJobAsync(int count, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<DashboardProjectInfo>>([]);
		public Task<DashboardJobMetrics> GetDashboardJobMetricsAsync(int rangeDays, CancellationToken cancellationToken = default)
			=> Task.FromResult(new DashboardJobMetrics
			{
				RangeDays = rangeDays,
				Buckets = []
			});
		public Task<IEnumerable<DashboardRunningJobInfo>> GetDashboardRunningJobsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<DashboardRunningJobInfo>>([]);
	}

	private sealed class FakeDatabaseService : IDatabaseService
	{
		public Task<DatabaseExportDto> ExportAsync(CancellationToken ct = default) => Task.FromResult(new DatabaseExportDto());
		public Task<DatabaseConfigurationInfo> GetConfigurationAsync(CancellationToken ct = default) => Task.FromResult(new DatabaseConfigurationInfo
		{
			Provider = "sqlite",
			ConnectionStringPreview = "/tmp/vibeswarm.db",
			ConfigurationSource = "Application configuration",
			RuntimeConfigurationPath = "/tmp/database.runtime.json",
			CanUpdateConfiguration = true
		});
		public Task<DatabaseImportResult> ImportAsync(DatabaseExportDto export, CancellationToken ct = default) => throw new NotSupportedException();
		public Task<DatabaseStorageSummary> GetStorageSummaryAsync(CancellationToken ct = default) => Task.FromResult(new DatabaseStorageSummary
		{
			Provider = "sqlite",
			TotalSizeBytes = 1024 * 1024,
			JobsCount = 2,
			JobMessagesCount = 4,
			ProviderUsageRecordsCount = 1
		});
		public Task<DatabaseMigrationResult> MigrateAsync(DatabaseMigrationRequest request, CancellationToken ct = default) => throw new NotSupportedException();
		public Task<DatabaseMaintenanceResult> RunMaintenanceAsync(DatabaseMaintenanceRequest request, CancellationToken ct = default) => throw new NotSupportedException();
	}

	private sealed class NoOpJsRuntime : IJSRuntime
	{
		private readonly Dictionary<string, string?> _sessionStorage;

		public NoOpJsRuntime(IReadOnlyDictionary<string, string?>? sessionStorage = null)
		{
			_sessionStorage = sessionStorage == null
				? new Dictionary<string, string?>()
				: new Dictionary<string, string?>(sessionStorage);
		}

		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
			=> InvokeAsync<TValue>(identifier, CancellationToken.None, args);

		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
		{
			if (string.Equals(identifier, "sessionStorage.getItem", StringComparison.Ordinal))
			{
				var key = args is { Length: > 0 } ? args[0]?.ToString() : null;
				if (!string.IsNullOrEmpty(key) && _sessionStorage.TryGetValue(key, out var storedValue))
				{
					return ValueTask.FromResult((TValue)(object?)storedValue!);
				}

				return ValueTask.FromResult((TValue)(object?)null!);
			}

			if (string.Equals(identifier, "sessionStorage.setItem", StringComparison.Ordinal))
			{
				var key = args is { Length: > 0 } ? args[0]?.ToString() : null;
				if (!string.IsNullOrEmpty(key))
				{
					_sessionStorage[key] = args is { Length: > 1 } ? args[1]?.ToString() : null;
				}

				return ValueTask.FromResult(default(TValue)!);
			}

			if (string.Equals(identifier, "sessionStorage.removeItem", StringComparison.Ordinal))
			{
				var key = args is { Length: > 0 } ? args[0]?.ToString() : null;
				if (!string.IsNullOrEmpty(key))
				{
					_sessionStorage.Remove(key);
				}

				return ValueTask.FromResult(default(TValue)!);
			}

			return ValueTask.FromResult(default(TValue)!);
		}
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
