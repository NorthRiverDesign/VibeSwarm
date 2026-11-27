using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using VibeSwarm.Client.Components.Common;
using VibeSwarm.Client.Components.Ideas;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Validation;

namespace VibeSwarm.Tests;

public sealed class SuggestIdeasModalTests
{
	[Fact]
	public async Task RenderedSuggestIdeasModal_ShowsSourceToggleAndProviderInputs_WhenVisible()
	{
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		var inferenceProviders = new List<InferenceProvider>
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
		var providers = new List<Provider>
		{
			new()
			{
				Id = Guid.NewGuid(),
				Name = "Claude CLI",
				Type = ProviderType.Claude,
				IsEnabled = true,
				AvailableModels =
				[
					new ProviderModel { Id = Guid.NewGuid(), ProviderId = Guid.NewGuid(), ModelId = "claude-sonnet-4.6", DisplayName = "Claude Sonnet 4.6", IsAvailable = true, IsDefault = true }
				]
			}
		};

		var html = await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
			{
				[nameof(SuggestIdeasModal.IsVisible)] = true,
				[nameof(SuggestIdeasModal.UseInference)] = true,
				[nameof(SuggestIdeasModal.HasInference)] = true,
				[nameof(SuggestIdeasModal.AvailableInferenceProviders)] = inferenceProviders,
				[nameof(SuggestIdeasModal.AvailableProviders)] = providers,
				[nameof(SuggestIdeasModal.SelectedProviderId)] = inferenceProviders[0].Id,
				[nameof(SuggestIdeasModal.SelectedModelId)] = "qwen2.5-coder:7b",
				[nameof(SuggestIdeasModal.IdeaCount)] = 4,
				[nameof(SuggestIdeasModal.Focus)] = "Onboarding for new admins"
			});

			var output = await renderer.RenderComponentAsync<SuggestIdeasModal>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("Suggest Ideas", html);
		// One profile choice stands in for the inference switch, the provider and the model.
		Assert.Contains("id=\"suggestIdeasProfile\"", html);
		Assert.DoesNotContain("Use inference", html);
		Assert.DoesNotContain("id=\"suggestIdeasProvider\"", html);
		Assert.DoesNotContain("id=\"suggestIdeasModel\"", html);
		Assert.Contains("Ideas to Generate", html);
		Assert.Contains(">4<", html);
		Assert.Contains("id=\"suggestIdeasFocus\"", html);
		Assert.Contains($"maxlength=\"{ValidationLimits.IdeaSuggestionFocusMaxLength}\"", html);
		Assert.Contains("Onboarding for new admins", html);
	}

	[Fact]
	public void TypingAFocus_RaisesFocusChanged()
	{
		using var context = new BunitContext();
		context.JSInterop.SetupModule(ModalDialog.InteropModulePath).Mode = JSRuntimeMode.Loose;
		var inferenceProvider = new InferenceProvider { Id = Guid.NewGuid(), Name = "Local Ollama", Endpoint = "http://ollama:11434", IsEnabled = true };
		string? focus = null;

		var cut = context.Render<SuggestIdeasModal>(parameters => parameters
			.Add(component => component.IsVisible, true)
			.Add(component => component.HasInference, true)
			.Add(component => component.AvailableInferenceProviders, [inferenceProvider])
			.Add(component => component.SelectedProviderId, inferenceProvider.Id)
			.Add(component => component.FocusChanged, EventCallback.Factory.Create<string?>(this, value => focus = value)));

		cut.Find("#suggestIdeasFocus").Input("Mobile layout ");

		cut.WaitForAssertion(() => Assert.Equal("Mobile layout ", focus));
	}

	[Fact]
	public async Task RenderedSuggestIdeasModal_UsesConfiguredProviderMode_WhenOnlyProvidersAreAvailable()
	{
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		var providerId = Guid.NewGuid();
		var providers = new List<Provider>
		{
			new()
			{
				Id = providerId,
				Name = "Copilot CLI",
				Type = ProviderType.Copilot,
				IsEnabled = true,
				AvailableModels =
				[
					new ProviderModel
					{
						Id = Guid.NewGuid(),
						ProviderId = providerId,
						ModelId = "gpt-5.4",
						DisplayName = "GPT-5.4",
						IsAvailable = true,
						IsDefault = true
					}
				]
			}
		};

		var html = await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
			{
				[nameof(SuggestIdeasModal.IsVisible)] = true,
				[nameof(SuggestIdeasModal.UseInference)] = true,
				[nameof(SuggestIdeasModal.HasInference)] = false,
				[nameof(SuggestIdeasModal.AvailableProviders)] = providers,
				[nameof(SuggestIdeasModal.IdeaCount)] = 3
			});

			var output = await renderer.RenderComponentAsync<SuggestIdeasModal>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("id=\"suggestIdeasProfile\"", html);
		Assert.DoesNotContain("Use inference", html);
		Assert.DoesNotContain("Inference Provider", html);
	}

	[Fact]
	public void PickingAProfile_SetsTheKindProviderAndModelTheRequestSends()
	{
		using var context = new BunitContext();
		context.JSInterop.SetupModule(ModalDialog.InteropModulePath).Mode = JSRuntimeMode.Loose;
		var ollama = new InferenceProvider { Id = Guid.NewGuid(), Name = "Local Ollama", Endpoint = "http://ollama:11434", IsEnabled = true,
			Models = [new InferenceModel { Id = Guid.NewGuid(), ModelId = "qwen", IsAvailable = true }] };
		var claude = new Provider { Id = Guid.NewGuid(), Name = "Claude CLI", Type = ProviderType.Claude, IsEnabled = true,
			AvailableModels = [new ProviderModel { Id = Guid.NewGuid(), ModelId = "sonnet", IsAvailable = true }] };
		var localIdeas = new Profile { Id = Guid.NewGuid(), Name = "Local ideas", InferenceProviderId = ollama.Id, InferenceProvider = ollama, ModelId = "qwen" };
		var careful = new Profile { Id = Guid.NewGuid(), Name = "Careful", ProviderId = claude.Id, Provider = claude, ModelId = "sonnet" };
		// A profile on a provider this dialog wasn't given can't be asked.
		var elsewhere = new Profile { Id = Guid.NewGuid(), Name = "Elsewhere", ProviderId = Guid.NewGuid(), Provider = new Provider { Name = "Other", IsEnabled = true } };
		context.Services.AddSingleton<VibeSwarm.Shared.Services.IProfileService>(new FakeProfileService(localIdeas, careful, elsewhere));
		(bool? UseInference, Guid? ProviderId, string? ModelId) sent = (null, null, "unset");

		var cut = context.Render<SuggestIdeasModal>(parameters => parameters
			.Add(modal => modal.IsVisible, true)
			.Add(modal => modal.UseInference, true)
			.Add(modal => modal.HasInference, true)
			.Add(modal => modal.AvailableInferenceProviders, [ollama])
			.Add(modal => modal.AvailableProviders, [claude])
			.Add(modal => modal.SelectedProviderId, ollama.Id)
			.Add(modal => modal.SelectedModelId, "qwen")
			.Add(modal => modal.UseInferenceChanged, EventCallback.Factory.Create<bool>(this, value => sent.UseInference = value))
			.Add(modal => modal.SelectedProviderIdChanged, EventCallback.Factory.Create<Guid?>(this, value => sent.ProviderId = value))
			.Add(modal => modal.SelectedModelIdChanged, EventCallback.Factory.Create<string?>(this, value => sent.ModelId = value)));

		// It opens on the profile holding what the dialog started with.
		cut.WaitForAssertion(() => Assert.Equal(localIdeas.Id.ToString(), cut.Find("#suggestIdeasProfile").GetAttribute("value")));
		Assert.Equal(["Local ideas", "Careful"], cut.FindAll("#suggestIdeasProfile option").Select(option => option.TextContent));
		Assert.Empty(cut.FindAll("#useInferenceForSuggestions, #suggestIdeasProvider, #suggestIdeasModel"));

		cut.Find("#suggestIdeasProfile").Change(careful.Id.ToString());

		Assert.Equal((false, (Guid?)claude.Id, "sonnet"), sent);
	}

	private sealed class FakeProfileService(params Profile[] profiles) : VibeSwarm.Shared.Services.IProfileService
	{
		public Task<IEnumerable<Profile>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Profile>>(profiles);
		public Task<Profile?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(profiles.FirstOrDefault(profile => profile.Id == id));
		public Task<Profile> CreateAsync(Profile profile, CancellationToken cancellationToken = default) => Task.FromResult(profile);
		public Task<Profile> UpdateAsync(Profile profile, CancellationToken cancellationToken = default) => Task.FromResult(profile);
		public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
	}

	private sealed class NoOpJsRuntime : IJSRuntime
	{
		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
			=> ValueTask.FromResult(default(TValue)!);

		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
			=> ValueTask.FromResult(default(TValue)!);
	}
}
