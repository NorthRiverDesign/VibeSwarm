using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using VibeSwarm.Client.Components.Providers;
using VibeSwarm.Client.Services;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Inference;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

/// <summary>
/// Model refresh used to no-op until the provider had been saved, because it keyed off the
/// provider's database id. These cover discovery for an unsaved provider and the rule that
/// Grok needs an API key first while Ollama does not.
/// </summary>
public sealed class InferenceProviderModalRefreshTests
{
	private const string RefreshButtonSelector = "#inference-model + button";

	[Fact]
	public void AddingOllamaProvider_RefreshIsAvailableWithoutAnApiKey()
	{
		using var context = Build(out _, out _);
		var cut = RenderModal(context);

		var refresh = cut.Find(RefreshButtonSelector);

		Assert.False(refresh.HasAttribute("disabled"));
	}

	[Fact]
	public void AddingOllamaProvider_RefreshDiscoversModelsBeforeTheProviderIsSaved()
	{
		using var context = Build(out var inference, out var providers);
		inference.ProbeResult = new InferenceHealthResult
		{
			IsAvailable = true,
			DiscoveredModels =
			[
				new DiscoveredModel { Name = "qwen3", DisplayName = "Qwen 3", ParameterSize = "7B" },
				new DiscoveredModel { Name = "llama3.2", DisplayName = "Llama 3.2" }
			]
		};

		var cut = RenderModal(context);
		cut.Find(RefreshButtonSelector).Click();

		cut.WaitForAssertion(() => Assert.Contains("Qwen 3", cut.Markup));

		// Discovery went through the probe (no saved row), not the id-keyed refresh.
		Assert.Equal(1, inference.ProbeCallCount);
		Assert.Equal(0, providers.RefreshModelsCallCount);
		Assert.Equal("http://localhost:11434", inference.LastProbe?.Endpoint);
		Assert.Equal(InferenceProviderType.Ollama, inference.LastProbe?.ProviderType);
		Assert.Contains("Llama 3.2", cut.Markup);
	}

	[Fact]
	public void AddingGrokProvider_RefreshIsBlockedUntilAnApiKeyIsEntered()
	{
		using var context = Build(out var inference, out _);
		var cut = RenderModal(context);

		cut.Find("#inference-type").Change(nameof(InferenceProviderType.Grok));

		var refresh = cut.Find(RefreshButtonSelector);
		Assert.True(refresh.HasAttribute("disabled"));
		Assert.Contains("Enter an API key to discover Grok models.", cut.Markup);

		refresh.Click();
		Assert.Equal(0, inference.ProbeCallCount);
	}

	[Fact]
	public void AddingGrokProvider_RefreshSendsTheTypedApiKeyOnceEntered()
	{
		using var context = Build(out var inference, out _);
		inference.ProbeResult = new InferenceHealthResult
		{
			IsAvailable = true,
			DiscoveredModels = [new DiscoveredModel { Name = "grok-4", DisplayName = "Grok 4" }]
		};

		var cut = RenderModal(context);
		cut.Find("#inference-type").Change(nameof(InferenceProviderType.Grok));
		cut.Find("#inference-apikey").Input("xai-test-key");

		var refresh = cut.Find(RefreshButtonSelector);
		Assert.False(refresh.HasAttribute("disabled"));

		refresh.Click();

		cut.WaitForAssertion(() => Assert.Contains("Grok 4", cut.Markup));
		Assert.Equal("xai-test-key", inference.LastProbe?.ApiKey);
		Assert.Equal(InferenceProviderType.Grok, inference.LastProbe?.ProviderType);
	}

	[Fact]
	public void EditingSavedProvider_RefreshPersistsThroughTheProviderRecord()
	{
		var providerId = Guid.NewGuid();
		var saved = new InferenceProvider
		{
			Id = providerId,
			Name = "Local Ollama",
			ProviderType = InferenceProviderType.Ollama,
			Endpoint = "http://ollama:11434",
			IsEnabled = true
		};

		using var context = Build(out var inference, out var providers);
		providers.RefreshedModels =
		[
			new InferenceModel
			{
				InferenceProviderId = providerId,
				ModelId = "qwen3",
				DisplayName = "Qwen 3",
				IsAvailable = true,
				IsDefault = true,
				TaskType = "default"
			}
		];

		var cut = context.Render<InferenceProviderModal>(parameters => parameters
			.Add(component => component.IsVisible, true)
			.Add(component => component.EditProvider, saved));

		cut.Find(RefreshButtonSelector).Click();

		cut.WaitForAssertion(() => Assert.Contains("Qwen 3", cut.Markup));

		// A saved provider must go through the persisting path, not the in-memory probe.
		Assert.Equal(1, providers.RefreshModelsCallCount);
		Assert.Equal(providerId, providers.LastRefreshedProviderId);
		Assert.Equal(0, inference.ProbeCallCount);
	}

	private static IRenderedComponent<InferenceProviderModal> RenderModal(BunitContext context) =>
		context.Render<InferenceProviderModal>(parameters => parameters
			.Add(component => component.IsVisible, true));

	private static BunitContext Build(out RecordingInferenceService inference, out RecordingProviderService providers)
	{
		var context = new BunitContext();
		inference = new RecordingInferenceService();
		providers = new RecordingProviderService();

		context.Services.AddLogging();
		context.Services.AddSingleton<IInferenceService>(inference);
		context.Services.AddSingleton<IInferenceProviderService>(providers);
		context.Services.AddSingleton<NotificationService>();
		context.Services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());

		return context;
	}

	private sealed class NoOpJsRuntime : IJSRuntime
	{
		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
			=> ValueTask.FromResult(default(TValue)!);

		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
			=> ValueTask.FromResult(default(TValue)!);
	}

	private sealed class RecordingInferenceService : IInferenceService
	{
		public InferenceProbeRequest? LastProbe { get; private set; }
		public int ProbeCallCount { get; private set; }
		public InferenceHealthResult ProbeResult { get; set; } = new() { IsAvailable = true };

		public Task<InferenceHealthResult> ProbeAsync(InferenceProbeRequest request, CancellationToken ct = default)
		{
			ProbeCallCount++;
			LastProbe = request;
			return Task.FromResult(ProbeResult);
		}

		public Task<InferenceHealthResult> CheckHealthAsync(string? endpoint = null, InferenceProviderType? providerType = null, CancellationToken ct = default)
			=> Task.FromResult(new InferenceHealthResult());
		public Task<List<DiscoveredModel>> GetAvailableModelsAsync(string? endpoint = null, InferenceProviderType? providerType = null, CancellationToken ct = default)
			=> Task.FromResult(new List<DiscoveredModel>());
		public Task<InferenceResponse> GenerateAsync(InferenceRequest request, CancellationToken ct = default) => throw new NotSupportedException();
		public Task<InferenceResponse> GenerateForTaskAsync(string taskType, string prompt, string? systemPrompt = null, CancellationToken ct = default) => throw new NotSupportedException();
	}

	private sealed class RecordingProviderService : FakeInferenceProviderServiceBase
	{
		public IReadOnlyList<InferenceModel> RefreshedModels { get; set; } = [];
		public int RefreshModelsCallCount { get; private set; }
		public Guid? LastRefreshedProviderId { get; private set; }

		public override Task<IEnumerable<InferenceModel>> RefreshModelsAsync(Guid providerId, CancellationToken ct = default)
		{
			RefreshModelsCallCount++;
			LastRefreshedProviderId = providerId;
			return Task.FromResult<IEnumerable<InferenceModel>>(RefreshedModels);
		}

		public override Task<IEnumerable<InferenceModel>> GetModelsAsync(Guid providerId, CancellationToken ct = default)
			=> Task.FromResult<IEnumerable<InferenceModel>>([]);
		public override Task<IEnumerable<InferenceProvider>> GetAllAsync(CancellationToken ct = default)
			=> Task.FromResult<IEnumerable<InferenceProvider>>([]);
		public override Task<InferenceProvider?> GetByIdAsync(Guid id, CancellationToken ct = default)
			=> Task.FromResult<InferenceProvider?>(null);
		public override Task<IEnumerable<InferenceProvider>> GetEnabledAsync(CancellationToken ct = default)
			=> Task.FromResult<IEnumerable<InferenceProvider>>([]);
		public override Task SetModelForTaskAsync(Guid providerId, string modelId, string taskType, CancellationToken ct = default) => Task.CompletedTask;
		public override Task<InferenceModel?> GetModelForTaskAsync(string taskType, CancellationToken ct = default)
			=> Task.FromResult<InferenceModel?>(null);
	}
}
