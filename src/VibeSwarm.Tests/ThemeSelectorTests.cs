using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Client.Components.Common;
using VibeSwarm.Shared.Data;

namespace VibeSwarm.Tests;

public sealed class ThemeSelectorTests
{
	[Fact]
	public async Task ThemeSelector_HighlightsSelectedTheme()
	{
		var services = new ServiceCollection();
		services.AddLogging();

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		var html = await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
			{
				[nameof(ThemeSelector.SelectedTheme)] = ThemePreference.Dark
			});

			var output = await renderer.RenderComponentAsync<ThemeSelector>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("Theme", html);
		Assert.Contains("bi-moon-stars-fill", html);
		Assert.Contains("btn btn-primary", html);
		Assert.Contains(">Dark</span>", html, StringComparison.Ordinal);
	}
}
