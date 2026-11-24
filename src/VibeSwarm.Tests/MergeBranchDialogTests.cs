using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using VibeSwarm.Client.Components.Git;
using VibeSwarm.Client.Services;
using VibeSwarm.Shared.VersionControl;
using VibeSwarm.Shared.VersionControl.Models;

namespace VibeSwarm.Tests;

public sealed class MergeBranchDialogTests
{
	private const string WorkingPath = "/src/app";

	[Fact]
	public void Opening_PreselectsThePreferredLocalTarget_AndPreviewsTheMerge()
	{
		var git = new RecordingVersionControlService();
		using var context = CreateContext(git);

		var cut = RenderDialog(context, preferred: [null, "deploy"]);

		cut.WaitForAssertion(() => Assert.Contains("can be merged into 'deploy'", cut.Markup));
		var options = cut.FindAll("#mergeTargetBranch option").Select(option => option.TextContent.Trim()).ToList();
		// Remote branches and the current branch aren't merge targets.
		Assert.Equal(["deploy", "main"], options);
		Assert.Equal([("develop", "deploy")], git.Previews);
		Assert.DoesNotContain(MergeBranchDialog.UncommittedChangesNotice, cut.Markup);
	}

	[Fact]
	public void MergeAndPush_MergesTheCurrentBranchIntoTheChosenTarget_ThenCloses()
	{
		var git = new RecordingVersionControlService();
		using var context = CreateContext(git);
		var closes = 0;
		string? mergedInto = null;

		var cut = RenderDialog(context, preferred: ["main"],
			onClose: () => closes++, onMerged: target => mergedInto = target);
		cut.WaitForAssertion(() => Assert.Contains("can be merged into 'main'", cut.Markup));

		cut.Find("#mergeTargetBranch").Change("deploy");
		cut.WaitForAssertion(() => Assert.Contains("can be merged into 'deploy'", cut.Markup));
		cut.FindAll("button").Single(button => button.TextContent.Contains("Merge & Push")).Click();

		cut.WaitForAssertion(() => Assert.Equal("deploy", mergedInto));
		Assert.Equal([("develop", "deploy", true)], git.Merges);
		Assert.True(closes > 0);
		var toast = Assert.Single(context.Services.GetRequiredService<NotificationService>().Notifications);
		Assert.Equal(NotificationType.Success, toast.Type);
		Assert.Contains("Merged 'develop' into 'deploy'.", toast.Message);
	}

	[Fact]
	public void Opening_WarnsWhenTheCheckoutHasUncommittedWork()
	{
		var git = new RecordingVersionControlService { HasUncommittedChanges = true };
		using var context = CreateContext(git);

		var cut = RenderDialog(context, preferred: ["main"]);

		cut.WaitForAssertion(() => Assert.Contains(MergeBranchDialog.UncommittedChangesNotice, cut.Markup));
	}

	[Fact]
	public void Conflicts_ShowTheResolverAndAreSentWithTheMerge()
	{
		var git = new RecordingVersionControlService
		{
			PreviewResult = GitOperationResult.Failed("Merge conflicts found.", mergeConflictFiles:
			[
				new MergeConflictFile { FileName = "app.txt", DiffContent = "@@ -1 +1 @@", Content = "<<<<<<< ours\na\n=======\nb\n>>>>>>> theirs" }
			])
		};
		using var context = CreateContext(git);
		var cut = RenderDialog(context, preferred: ["main"]);

		cut.WaitForAssertion(() => Assert.Contains("This merge needs manual conflict resolution.", cut.Markup));
		Assert.True(ResolveButton(cut).HasAttribute("disabled"));

		cut.Find("textarea").Input("resolved");
		cut.WaitForAssertion(() => Assert.False(ResolveButton(cut).HasAttribute("disabled")));
		ResolveButton(cut).Click();

		cut.WaitForAssertion(() => Assert.Single(git.Merges));
		var resolution = Assert.Single(git.LastResolutions!);
		Assert.Equal("app.txt", resolution.FileName);
		Assert.Equal("resolved", resolution.ResolvedContent);
	}

	private static AngleSharp.Dom.IElement ResolveButton(IRenderedComponent<MergeBranchDialog> cut)
		=> cut.FindAll("button").Single(button => button.TextContent.Contains("Resolve, Merge & Push"));

	private static IRenderedComponent<MergeBranchDialog> RenderDialog(BunitContext context, IReadOnlyList<string?> preferred,
		Action? onClose = null, Action<string>? onMerged = null)
		=> context.Render<MergeBranchDialog>(parameters => parameters
			.Add(dialog => dialog.IsVisible, true)
			.Add(dialog => dialog.WorkingPath, WorkingPath)
			.Add(dialog => dialog.ProjectName, "App")
			.Add(dialog => dialog.PreferredTargetBranches, preferred)
			.Add(dialog => dialog.IsVisibleChanged, EventCallback.Factory.Create<bool>(new object(), visible =>
			{
				if (!visible)
				{
					onClose?.Invoke();
				}
			}))
			.Add(dialog => dialog.OnMerged, EventCallback.Factory.Create<string>(new object(), target => onMerged?.Invoke(target))));

	private static BunitContext CreateContext(RecordingVersionControlService git)
	{
		var context = new BunitContext();
		context.JSInterop.Mode = JSRuntimeMode.Loose;
		context.Services.AddSingleton<IVersionControlService>(git);
		context.Services.AddSingleton<NotificationService>();
		return context;
	}

	private sealed class RecordingVersionControlService : FakeVersionControlServiceBase
	{
		public bool HasUncommittedChanges { get; init; }
		public GitOperationResult? PreviewResult { get; init; }
		public List<(string Source, string Target)> Previews { get; } = [];
		public List<(string Source, string Target, bool Push)> Merges { get; } = [];
		public IReadOnlyList<MergeConflictResolution>? LastResolutions { get; private set; }

		public override Task<string?> GetCurrentBranchAsync(string workingDirectory, CancellationToken cancellationToken = default)
			=> Task.FromResult<string?>("develop");

		public override Task<IReadOnlyList<GitBranchInfo>> GetBranchesAsync(string workingDirectory, bool includeRemote = true, CancellationToken cancellationToken = default)
			=> Task.FromResult<IReadOnlyList<GitBranchInfo>>(
			[
				new GitBranchInfo { Name = "develop", ShortName = "develop", IsCurrent = true },
				new GitBranchInfo { Name = "main", ShortName = "main" },
				new GitBranchInfo { Name = "deploy", ShortName = "deploy" },
				new GitBranchInfo { Name = "origin/release", ShortName = "release", IsRemote = true }
			]);

		public override Task<bool> HasUncommittedChangesAsync(string workingDirectory, CancellationToken cancellationToken = default)
			=> Task.FromResult(HasUncommittedChanges);

		public override Task<bool> IsGitHubCliAvailableAsync(CancellationToken cancellationToken = default)
			=> Task.FromResult(false);

		public override Task<GitOperationResult> PreviewMergeBranchAsync(string workingDirectory, string sourceBranch, string targetBranch, string remoteName = "origin", CancellationToken cancellationToken = default)
		{
			Previews.Add((sourceBranch, targetBranch));
			return Task.FromResult(PreviewResult ?? GitOperationResult.Succeeded(
				output: $"'{sourceBranch}' can be merged into '{targetBranch}' without conflicts.",
				changedFilesCount: 2));
		}

		public override Task<GitOperationResult> MergeBranchAsync(string workingDirectory, string sourceBranch, string targetBranch, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken cancellationToken = default, bool pushAfterMerge = true, IReadOnlyList<MergeConflictResolution>? conflictResolutions = null, bool preferRemoteSource = false)
		{
			Merges.Add((sourceBranch, targetBranch, pushAfterMerge));
			LastResolutions = conflictResolutions;
			return Task.FromResult(GitOperationResult.Succeeded(branchName: sourceBranch, targetBranch: targetBranch));
		}
	}
}
