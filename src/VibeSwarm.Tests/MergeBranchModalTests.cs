using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using VibeSwarm.Client.Components.Git;
using VibeSwarm.Shared.VersionControl.Models;

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

	[Fact]
	public async Task RenderedMergeBranchModal_ShowsConflictEditorAndResolveAction()
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
				[nameof(MergeBranchModal.TargetBranches)] = new List<string> { "main" },
				[nameof(MergeBranchModal.SelectedTargetBranch)] = "main",
				[nameof(MergeBranchModal.PushAfterMerge)] = false,
				[nameof(MergeBranchModal.IsGitHubCliAvailable)] = true,
				[nameof(MergeBranchModal.PreviewError)] = "Merge conflicts were detected.",
				[nameof(MergeBranchModal.ConflictFiles)] = new List<MergeConflictFile>
				{
					new()
					{
						FileName = "README.md",
						DiffContent = """
							diff --git a/README.md b/README.md
							--- a/README.md
							+++ b/README.md
							@@ -1,5 +1,5 @@
							 <<<<<<< HEAD
							 main
							 =======
							 feature
							 >>>>>>> feature/test
							""",
						Content = "<<<<<<< HEAD\nmain\n=======\nfeature\n>>>>>>> feature/test\n"
					}
				}
			});

			var output = await renderer.RenderComponentAsync<MergeBranchModal>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("manual conflict resolution", html);
		Assert.Contains("Conflicted Files", html);
		Assert.Contains("README.md", html);
		Assert.Contains("Resolved content", html);
		Assert.Contains("Resolve & Merge", html);
		Assert.Contains("Resolve conflicts locally first", html);
	}

	private sealed class NoOpJsRuntime : IJSRuntime
	{
		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
			=> ValueTask.FromResult(default(TValue)!);

		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
			=> ValueTask.FromResult(default(TValue)!);
	}
}
