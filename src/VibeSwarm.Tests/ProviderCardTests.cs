using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Client.Components.Providers;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Tests;

public sealed class ProviderCardTests
{
	[Fact]
	public async Task ProviderCard_ShowsLatestUsageFallbackMessage_WhenNoMaxBudgetIsAvailable()
	{
		var services = new ServiceCollection();
		services.AddLogging();

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
		{
			[nameof(ProviderCard.Name)] = "Claude",
			[nameof(ProviderCard.ProviderType)] = "Claude",
			[nameof(ProviderCard.ConnectionMode)] = "CLI",
			[nameof(ProviderCard.IsEnabled)] = true,
			[nameof(ProviderCard.UsageSummary)] = new ProviderUsageSummary
			{
				LimitType = UsageLimitType.RateLimit,
				CurrentUsage = 72,
				LimitMessage = "Weekly limit 72/100 used",
				LastUpdatedAt = DateTime.UtcNow
			}
		});

		var html = await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var output = await renderer.RenderComponentAsync<ProviderCard>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("Latest limit usage: 72", html);
		Assert.Contains("Weekly limit 72/100 used", html);
	}
}
