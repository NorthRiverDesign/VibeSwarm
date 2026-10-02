using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using VibeSwarm.Client.Components.Providers;
using VibeSwarm.Client.Services;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

/// <summary>
/// The refresh button beside every model select: it refreshes the right kind of provider,
/// hands the new list back to the select's owner and reports the outcome as a toast.
/// </summary>
public sealed class ModelRefreshButtonTests
{
	[Fact]
	public void WithoutAProvider_ButtonIsDisabled()
	{
		using var context = Build(out _, out _, out _);

		var cut = context.Render<ModelRefreshButton>(parameters => parameters
			.Add(component => component.ProviderId, (Guid?)null));

		Assert.True(cut.Find("button").HasAttribute("disabled"));
	}

	[Fact]
	public void CliProvider_RefreshPassesModelsBackAndToastsTheCount()
	{
		using var context = Build(out var providers, out var inference, out var notifications);
		var providerId = Guid.NewGuid();
		providers.RefreshedModels =
		[
			new ProviderModel { ModelId = "sonnet", IsAvailable = true },
			new ProviderModel { ModelId = "opus", IsAvailable = true },
			new ProviderModel { ModelId = "retired", IsAvailable = false }
		];
		List<ProviderModel>? received = null;

		var cut = context.Render<ModelRefreshButton>(parameters => parameters
			.Add(component => component.ProviderId, providerId)
			.Add(component => component.ProviderName, "Claude")
			.Add(component => component.OnRefreshed, EventCallback.Factory.Create<List<ProviderModel>>(this, models => received = models)));

		cut.Find("button").Click();

		cut.WaitForAssertion(() => Assert.NotNull(received));
		Assert.Equal(providerId, providers.LastRefreshedProviderId);
		Assert.Equal(0, inference.RefreshModelsCallCount);
		Assert.Equal(3, received!.Count);
		var toast = Assert.Single(notifications.NotificationHistory);
		Assert.Equal(NotificationType.Success, toast.Type);
		Assert.Equal("Claude", toast.Title);
		Assert.Equal("Found 2 model(s).", toast.Message);
	}

	[Fact]
	public void InferenceProvider_RefreshUsesTheInferenceService()
	{
		using var context = Build(out var providers, out var inference, out var notifications);
		var providerId = Guid.NewGuid();
		inference.RefreshedModels = [new InferenceModel { ModelId = "qwen3", IsAvailable = true }];
		List<InferenceModel>? received = null;

		var cut = context.Render<ModelRefreshButton>(parameters => parameters
			.Add(component => component.ProviderId, providerId)
			.Add(component => component.IsInference, true)
			.Add(component => component.OnInferenceRefreshed, EventCallback.Factory.Create<List<InferenceModel>>(this, models => received = models)));

		cut.Find("button").Click();

		cut.WaitForAssertion(() => Assert.NotNull(received));
		Assert.Equal(1, inference.RefreshModelsCallCount);
		Assert.Null(providers.LastRefreshedProviderId);
		Assert.Equal("qwen3", Assert.Single(received!).ModelId);
		Assert.Contains(notifications.NotificationHistory, toast => toast.Type == NotificationType.Success && toast.Message == "Found 1 model(s).");
	}

	[Fact]
	public void RefreshFindingNoModels_ShowsAWarning()
	{
		using var context = Build(out _, out _, out var notifications);

		var cut = context.Render<ModelRefreshButton>(parameters => parameters
			.Add(component => component.ProviderId, Guid.NewGuid()));

		cut.Find("button").Click();

		cut.WaitForAssertion(() => Assert.Contains(notifications.NotificationHistory, toast => toast.Type == NotificationType.Warning));
	}

	[Fact]
	public void FailedRefresh_ShowsAnErrorAndReEnablesTheButton()
	{
		using var context = Build(out var providers, out _, out var notifications);
		providers.RefreshException = new InvalidOperationException("CLI not installed");
		var raised = false;

		var cut = context.Render<ModelRefreshButton>(parameters => parameters
			.Add(component => component.ProviderId, Guid.NewGuid())
			.Add(component => component.OnRefreshed, EventCallback.Factory.Create<List<ProviderModel>>(this, _ => raised = true)));

		cut.Find("button").Click();

		cut.WaitForAssertion(() => Assert.Contains(notifications.NotificationHistory,
			toast => toast.Type == NotificationType.Error && toast.Message == "Failed to refresh models: CLI not installed"));
		Assert.False(raised);
		Assert.False(cut.Find("button").HasAttribute("disabled"));
	}

	private static BunitContext Build(out RecordingProviderService providers, out RecordingInferenceProviderService inference, out NotificationService notifications)
	{
		var context = new BunitContext();
		providers = new RecordingProviderService();
		inference = new RecordingInferenceProviderService();
		notifications = new NotificationService();
		context.Services.AddSingleton<IProviderService>(providers);
		context.Services.AddSingleton<IInferenceProviderService>(inference);
		context.Services.AddSingleton(notifications);
		return context;
	}

	private sealed class RecordingProviderService : FakeProviderServiceBase
	{
		public IReadOnlyList<ProviderModel> RefreshedModels { get; set; } = [];
		public Exception? RefreshException { get; set; }
		public Guid? LastRefreshedProviderId { get; private set; }

		public override Task<IEnumerable<ProviderModel>> RefreshModelsAsync(Guid providerId, CancellationToken cancellationToken = default)
		{
			LastRefreshedProviderId = providerId;
			return RefreshException != null
				? Task.FromException<IEnumerable<ProviderModel>>(RefreshException)
				: Task.FromResult<IEnumerable<ProviderModel>>(RefreshedModels);
		}
	}

	private sealed class RecordingInferenceProviderService : FakeInferenceProviderServiceBase
	{
		public IReadOnlyList<InferenceModel> RefreshedModels { get; set; } = [];
		public int RefreshModelsCallCount { get; private set; }

		public override Task<IEnumerable<InferenceModel>> RefreshModelsAsync(Guid providerId, CancellationToken ct = default)
		{
			RefreshModelsCallCount++;
			return Task.FromResult<IEnumerable<InferenceModel>>(RefreshedModels);
		}
	}
}
