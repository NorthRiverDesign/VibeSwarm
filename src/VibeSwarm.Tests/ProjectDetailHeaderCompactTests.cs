using Bunit;
using VibeSwarm.Client.Components.Projects;
using VibeSwarm.Shared.VersionControl.Models;

namespace VibeSwarm.Tests;

public sealed class ProjectDetailHeaderCompactTests
{
	[Theory]
	[InlineData(1, "1 commit behind origin")]
	[InlineData(4, "4 commits behind origin")]
	[InlineData(null, "Behind origin")]
	public void ShowsCommitsBehindOrigin(int? commitsBehind, string expected)
	{
		using var context = new BunitContext();
		context.JSInterop.Mode = JSRuntimeMode.Loose;

		var cut = RenderHeader(context, new GitRemoteSyncStatus { RemoteBranch = "origin/main", CommitsBehind = commitsBehind, IsRemoteReachable = true });

		var status = cut.FindAll("span.fg-warning").Single(span => span.TextContent.Trim() == expected);
		Assert.Contains("origin/main", status.GetAttribute("title"));
		Assert.Single(cut.FindAll("button"), button => button.TextContent.Trim() == "Sync");
	}

	[Fact]
	public void HidesBehindOrigin_WhenUpToDateOrBranchNotOnOrigin()
	{
		using var context = new BunitContext();
		context.JSInterop.Mode = JSRuntimeMode.Loose;

		var upToDate = RenderHeader(context, new GitRemoteSyncStatus { RemoteBranch = "origin/main", CommitsBehind = 0, IsRemoteReachable = true });
		var notOnOrigin = RenderHeader(context, new GitRemoteSyncStatus { IsRemoteReachable = true });

		Assert.DoesNotContain("behind origin", upToDate.Markup, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("behind origin", notOnOrigin.Markup, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(upToDate.FindAll("button"), button => button.TextContent.Trim() == "Sync");
		Assert.DoesNotContain(notOnOrigin.FindAll("button"), button => button.TextContent.Trim() == "Sync");
	}

	[Fact]
	public void BehindOrigin_SaysWhenCountedFromLastFetch()
	{
		using var context = new BunitContext();
		context.JSInterop.Mode = JSRuntimeMode.Loose;

		var cut = RenderHeader(context, new GitRemoteSyncStatus { RemoteBranch = "origin/main", CommitsBehind = 2, IsRemoteReachable = false });

		var status = cut.FindAll("span.fg-warning").Single(span => span.TextContent.Trim() == "2 commits behind origin");
		Assert.Contains("last fetch", status.GetAttribute("title"));
	}

	[Fact]
	public void BehindOrigin_SyncButtonSyncsInOneTap()
	{
		using var context = new BunitContext();
		context.JSInterop.Mode = JSRuntimeMode.Loose;
		var syncCount = 0;

		var cut = context.Render<ProjectDetailHeaderCompact>(parameters => parameters
			.Add(p => p.Name, "Demo Project")
			.Add(p => p.IsGitRepository, true)
			.Add(p => p.CurrentBranch, "main")
			.Add(p => p.RemoteSyncStatus, new GitRemoteSyncStatus { RemoteBranch = "origin/main", CommitsBehind = 2, IsRemoteReachable = true })
			.Add(p => p.OnSync, () => syncCount++));

		cut.FindAll("button").Single(button => button.TextContent.Trim() == "Sync").Click();

		Assert.Equal(1, syncCount);
	}

	[Fact]
	public void BehindOrigin_SyncButtonShowsProgressAndIsDisabledWhileSyncing()
	{
		using var context = new BunitContext();
		context.JSInterop.Mode = JSRuntimeMode.Loose;

		var cut = context.Render<ProjectDetailHeaderCompact>(parameters => parameters
			.Add(p => p.Name, "Demo Project")
			.Add(p => p.IsGitRepository, true)
			.Add(p => p.CurrentBranch, "main")
			.Add(p => p.IsGitOperationInProgress, true)
			.Add(p => p.IsSyncing, true)
			.Add(p => p.RemoteSyncStatus, new GitRemoteSyncStatus { RemoteBranch = "origin/main", CommitsBehind = 2, IsRemoteReachable = true }));

		var button = cut.FindAll("button").Single(button => button.TextContent.Trim() == "Syncing…");
		Assert.True(button.HasAttribute("disabled"));
		Assert.NotNull(button.QuerySelector(".spinner-border"));
	}

	[Fact]
	public void BehindOrigin_SyncButtonIsDisabledButNotSyncing_DuringAnotherGitOperation()
	{
		using var context = new BunitContext();
		context.JSInterop.Mode = JSRuntimeMode.Loose;

		var cut = context.Render<ProjectDetailHeaderCompact>(parameters => parameters
			.Add(p => p.Name, "Demo Project")
			.Add(p => p.IsGitRepository, true)
			.Add(p => p.CurrentBranch, "main")
			.Add(p => p.IsGitOperationInProgress, true)
			.Add(p => p.RemoteSyncStatus, new GitRemoteSyncStatus { RemoteBranch = "origin/main", CommitsBehind = 2, IsRemoteReachable = true }));

		var button = cut.FindAll("button").Single(button => button.TextContent.Trim() == "Sync");
		Assert.True(button.HasAttribute("disabled"));
	}

	private static IRenderedComponent<ProjectDetailHeaderCompact> RenderHeader(BunitContext context, GitRemoteSyncStatus status) =>
		context.Render<ProjectDetailHeaderCompact>(parameters => parameters
			.Add(p => p.Name, "Demo Project")
			.Add(p => p.IsGitRepository, true)
			.Add(p => p.CurrentBranch, "main")
			.Add(p => p.RemoteSyncStatus, status));
}
