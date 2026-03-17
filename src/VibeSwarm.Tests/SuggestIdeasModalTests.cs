using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using VibeSwarm.Client.Components.Ideas;
using VibeSwarm.Shared.Data;

namespace VibeSwarm.Tests;

public sealed class SuggestIdeasModalTests
{
	[Fact]
	public async Task RenderedSuggestIdeasModal_ShowsProviderModelAndCountInputs_WhenVisible()
	{
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		var providers = new List<InferenceProvider>
		{
			new()
			{
				Id = Guid.NewGuid(),
				Name = "Local Ollama A",
				Endpoint = "http://ollama-a:11434",
				IsEnabled = true,
				Models =
				[
					new InferenceModel { Id = Guid.NewGuid(), ModelId = "qwen2.5-coder:7b", DisplayName = "Qwen 2.5 Coder 7B", IsAvailable = true, IsDefault = true, TaskType = "suggest" },
					new InferenceModel { Id = Guid.NewGuid(), ModelId = "llama3.2", DisplayName = "Llama 3.2", IsAvailable = true, IsDefault = false, TaskType = "default" }
				]
			},
			new() { Id = Guid.NewGuid(), Name = "Local Ollama B", Endpoint = "http://ollama-b:11434", IsEnabled = true }
		};

		var html = await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
			{
				[nameof(SuggestIdeasModal.IsVisible)] = true,
				[nameof(SuggestIdeasModal.AvailableProviders)] = providers,
				[nameof(SuggestIdeasModal.SelectedProviderId)] = providers[0].Id,
				[nameof(SuggestIdeasModal.SelectedModelId)] = "qwen2.5-coder:7b",
				[nameof(SuggestIdeasModal.IdeaCount)] = 4
			});

			var output = await renderer.RenderComponentAsync<SuggestIdeasModal>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("Suggest Ideas", html);
		Assert.Contains("Inference Provider", html);
		Assert.Contains("Model", html);
		Assert.Contains("Ideas to Generate", html);
		Assert.Contains("Local Ollama A", html);
		Assert.Contains("Local Ollama B", html);
		Assert.Contains("Use provider default model", html);
		Assert.Contains("Qwen 2.5 Coder 7B (Default)", html);
		Assert.Contains("Llama 3.2", html);
		Assert.Contains(">4<", html);
	}

	private sealed class NoOpJsRuntime : IJSRuntime
	{
		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
			=> ValueTask.FromResult(default(TValue)!);

		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
			=> ValueTask.FromResult(default(TValue)!);
	}
}
