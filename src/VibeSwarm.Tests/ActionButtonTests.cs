using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Client.Components.Common;

namespace VibeSwarm.Tests;

public sealed class ActionButtonTests
{
	[Fact]
	public async Task RenderedActionButton_UsesFormAndLoadingText()
	{
		var services = new ServiceCollection();
		services.AddLogging();

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		var html = await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
			{
				[nameof(ActionButton.Type)] = "submit",
				[nameof(ActionButton.Form)] = "create-job-form",
				[nameof(ActionButton.Style)] = ActionButton.ButtonStyle.Primary,
				[nameof(ActionButton.Text)] = "Create Job",
				[nameof(ActionButton.LoadingText)] = "Creating...",
				[nameof(ActionButton.IsLoading)] = true
			});

			var output = await renderer.RenderComponentAsync<ActionButton>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("type=\"submit\"", html);
		Assert.Contains("form=\"create-job-form\"", html);
		Assert.Contains("btn btn-primary", html);
		Assert.Contains("Creating...", html);
		Assert.DoesNotContain("Create Job", html);
	}
}
