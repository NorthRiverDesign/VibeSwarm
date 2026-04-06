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

		Assert.Contains("vs-modal-container", html);
		Assert.Contains("custom-container", html);
		Assert.Contains("vs-modal-dialog", html);
		Assert.Contains("custom-dialog", html);
		Assert.Contains("modal-dialog-scrollable", html);
		Assert.Contains("vs-modal-content", html);
		Assert.Contains("custom-content", html);
		Assert.Contains("modal-body", html);
		Assert.Contains("vs-modal-body", html);
		Assert.Contains("custom-body", html);
		Assert.Contains("Modal Test", html);
	}

	[Fact]
	public void SiteCss_UsesStrongerModalBackdropBlur()
	{
		var css = File.ReadAllText(GetRepositoryPath("src", "VibeSwarm.Client", "wwwroot", "css", "site.css"));

		Assert.Contains("backdrop-filter: blur(40px);", css);
		Assert.Contains("-webkit-backdrop-filter: blur(40px);", css);
		Assert.Contains("backdrop-filter: blur(64px);", css);
		Assert.Contains("-webkit-backdrop-filter: blur(64px);", css);
	}

	private static string GetRepositoryPath(params string[] segments)
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);

		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VibeSwarm.sln")))
		{
			directory = directory.Parent;
		}

		Assert.NotNull(directory);
		return Path.Combine([directory.FullName, .. segments]);
	}

	private sealed class NoOpJsRuntime : IJSRuntime
	{
		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
			=> ValueTask.FromResult(default(TValue)!);

		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
			=> ValueTask.FromResult(default(TValue)!);
	}
}
