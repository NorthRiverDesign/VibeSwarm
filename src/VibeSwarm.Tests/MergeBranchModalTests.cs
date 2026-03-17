using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using VibeSwarm.Client.Components.Git;

namespace VibeSwarm.Tests;

public sealed class MergeBranchModalTests
{
	[Fact]
	public async Task RenderedMergeBranchModal_ShowsTargetSelectorPushOptionAndPreviewStatus()
	{
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		var html = await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
			{
				[nameof(MergeBranchModal.IsVisible)] = true,
				[nameof(MergeBranchModal.CurrentBranch)] = "feature/test",
				[nameof(MergeBranchModal.TargetBranches)] = new List<string> { "main", "release" },
				[nameof(MergeBranchModal.SelectedTargetBranch)] = "main",
				[nameof(MergeBranchModal.PushAfterMerge)] = true,
				[nameof(MergeBranchModal.PreviewMessage)] = "'feature/test' can be merged into 'main' without conflicts."
			});

			var output = await renderer.RenderComponentAsync<MergeBranchModal>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("Merge Branch", html);
		Assert.Contains("feature/test", html);
		Assert.Contains("Push target branch after merging", html);
		Assert.Contains("main", html);
		Assert.Contains("release", html);
		Assert.Contains("refreshes the target branch from origin", html);
		Assert.Contains("without conflicts", html);
		Assert.Contains("Merge & Push", html);
	}

	private sealed class NoOpJsRuntime : IJSRuntime
	{
		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
			=> ValueTask.FromResult(default(TValue)!);

		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
			=> ValueTask.FromResult(default(TValue)!);
	}
}
