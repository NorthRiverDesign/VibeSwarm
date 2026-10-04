using Bunit;
using Microsoft.Extensions.DependencyInjection;
using VibeSwarm.Client.Components.Providers;
using VibeSwarm.Client.Services;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

public sealed class ProviderModalSessionLimitTests
{
	private const string ThresholdSelector = "#modal-sessionPauseThreshold";

	[Fact]
	public void EditingAClaudeProvider_SavesTheSessionPauseThreshold()
	{
		using var context = Build(out var providers);
		var cut = context.Render<ProviderModal>(parameters => parameters
			.Add(component => component.IsVisible, true)
			.Add(component => component.EditProvider, NewProvider(ProviderType.Claude, pauseThresholdPercent: 75)));

		Assert.Equal("75", cut.Find(ThresholdSelector).GetAttribute("value"));

		cut.Find(ThresholdSelector).Change("80");
		cut.Find("form").Submit();

		cut.WaitForAssertion(() => Assert.Equal(80, providers.Updated?.SessionLimitPauseThresholdPercent));
	}

	[Fact]
	public void ProvidersWithoutSessions_HaveNoSessionPauseThreshold()
	{
		using var context = Build(out _);
		var cut = context.Render<ProviderModal>(parameters => parameters
			.Add(component => component.IsVisible, true)
			.Add(component => component.EditProvider, NewProvider(ProviderType.Copilot)));

		Assert.Empty(cut.FindAll(ThresholdSelector));
	}

	private static Provider NewProvider(ProviderType type, int? pauseThresholdPercent = null) => new()
	{
		Id = Guid.NewGuid(),
		Name = type.ToString(),
		Type = type,
		ConnectionMode = ProviderConnectionMode.CLI,
		IsEnabled = true,
		SessionLimitPauseThresholdPercent = pauseThresholdPercent
	};

	private static BunitContext Build(out RecordingProviderService providers)
	{
		var context = new BunitContext();
		context.JSInterop.Mode = JSRuntimeMode.Loose;
		providers = new RecordingProviderService();

		context.Services.AddLogging();
		context.Services.AddSingleton<IProviderService>(providers);
		context.Services.AddSingleton<NotificationService>();

		return context;
	}

	private sealed class RecordingProviderService : FakeProviderServiceBase
	{
		public Provider? Updated { get; private set; }

		public override Task<Provider> UpdateAsync(Provider provider, CancellationToken cancellationToken = default)
		{
			Updated = provider;
			return Task.FromResult(provider);
		}
	}
}
