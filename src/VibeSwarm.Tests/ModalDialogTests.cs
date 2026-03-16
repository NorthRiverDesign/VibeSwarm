using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using VibeSwarm.Client.Components.Common;

namespace VibeSwarm.Tests;

public sealed class ModalDialogTests
{
	[Fact]
	public async Task RenderedModalDialog_AppliesCustomContainerDialogContentAndBodyClasses()
	{
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		var html = await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
			{
				[nameof(ModalDialog.IsVisible)] = true,
				[nameof(ModalDialog.Title)] = "Modal Test",
				[nameof(ModalDialog.ContainerClass)] = "custom-container",
				[nameof(ModalDialog.DialogClass)] = "custom-dialog",
				[nameof(ModalDialog.ContentClass)] = "custom-content",
				[nameof(ModalDialog.BodyClass)] = "custom-body",
				[nameof(ModalDialog.ChildContent)] = (RenderFragment)(_ => _.AddContent(0, "Body"))
			});

			var output = await renderer.RenderComponentAsync<ModalDialog>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("vs-modal-container custom-container", html);
		Assert.Contains("vs-modal-dialog", html);
		Assert.Contains("custom-dialog", html);
		Assert.Contains("vs-modal-content custom-content", html);
		Assert.Contains("modal-body custom-body", html);
		Assert.Contains("Modal Test", html);
	}

	private sealed class NoOpJsRuntime : IJSRuntime
	{
		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
			=> ValueTask.FromResult(default(TValue)!);

		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
			=> ValueTask.FromResult(default(TValue)!);
	}
}
