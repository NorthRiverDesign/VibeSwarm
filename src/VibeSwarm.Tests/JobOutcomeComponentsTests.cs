using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using VibeSwarm.Client.Components.Jobs;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.VersionControl;

namespace VibeSwarm.Tests;

public sealed class JobOutcomeComponentsTests
{
	[Fact]
	public void JobOutcomeBadges_ShowsVerificationAndDeliveryBadges()
	{
		using var context = new BunitContext();

		var cut = context.Render<JobOutcomeBadges>(parameters => parameters
			.Add(component => component.Status, JobStatus.Completed)
			.Add(component => component.ChangedFilesCount, 3)
			.Add(component => component.BuildVerified, true)
			.Add(component => component.PullRequestNumber, 42)
			.Add(component => component.PullRequestUrl, "https://github.com/octo-org/octo-repo/pull/42"));

		Assert.Contains("3 files changed", cut.Markup);
		Assert.Contains("Checks passed", cut.Markup);
		Assert.Contains("PR #42", cut.Markup);
	}

	[Fact]
	public void JobOutcomeBadges_ShowsMergedBadgeWhenMergeRecorded()
	{
		using var context = new BunitContext();

		var cut = context.Render<JobOutcomeBadges>(parameters => parameters
			.Add(component => component.Status, JobStatus.Completed)
			.Add(component => component.PullRequestNumber, 42)
			.Add(component => component.PullRequestUrl, "https://github.com/octo-org/octo-repo/pull/42")
			.Add(component => component.MergedAt, DateTime.UtcNow));

		Assert.Contains("Merged", cut.Markup);
		Assert.Contains("PR #42", cut.Markup);
	}

	[Fact]
	public void JobOutcomeBadges_ShowsNeedsReviewWhenFailedChangesRemain()
	{
		using var context = new BunitContext();

		var cut = context.Render<JobOutcomeBadges>(parameters => parameters
			.Add(component => component.Status, JobStatus.Failed)
			.Add(component => component.ChangedFilesCount, 2));

		Assert.Contains("2 files changed", cut.Markup);
		Assert.Contains("Review changes", cut.Markup);
	}

	[Fact]
	public void JobOutcomeSummaryCard_ShowsVerificationFailureGuidance()
	{
		using var context = new BunitContext();

		var cut = context.Render<JobOutcomeSummaryCard>(parameters => parameters
			.Add(component => component.Status, JobStatus.Completed)
			.Add(component => component.ChangedFilesCount, 4)
			.Add(component => component.BuildVerified, false)
			.Add(component => component.BuildOutput, "dotnet test failed"));

		Assert.Contains("Verification blocked delivery", cut.Markup);
		Assert.Contains("Verification failed", cut.Markup);
		Assert.Contains("Checks failed", cut.Markup);
		Assert.Contains("Start with the failed checks", cut.Markup);
		Assert.Contains("Review verification output", cut.Markup);
		Assert.Contains("Show verification output", cut.Markup);
		Assert.Contains("dotnet test failed", cut.Markup);
	}

	[Fact]
	public void JobOutcomeSummaryCard_ShowsVerificationConfigurationAndDeliveryActions()
	{
		using var context = new BunitContext();

		var cut = context.Render<JobOutcomeSummaryCard>(parameters => parameters
			.Add(component => component.Status, JobStatus.Completed)
			.Add(component => component.ChangedFilesCount, 2)
			.Add(component => component.BuildVerificationEnabled, true)
			.Add(component => component.BuildCommand, "dotnet build")
			.Add(component => component.TestCommand, "dotnet test")
			.Add(component => component.DeliveryMode, GitChangeDeliveryMode.PullRequest)
			.Add(component => component.BranchName, "feature/outcome-card")
			.Add(component => component.TargetBranch, "main")
			.Add(component => component.ReviewChangesHref, "#job-changes-section")
			.Add(component => component.DeliveryHref, "#job-delivery-section"));

		Assert.Contains("Verification result missing", cut.Markup);
		Assert.Contains("Checks missing", cut.Markup);
		Assert.Contains("dotnet build", cut.Markup);
		Assert.Contains("dotnet test", cut.Markup);
		Assert.Contains("Ready for delivery", cut.Markup);
		Assert.Contains("Pull request flow", cut.Markup);
		Assert.Contains("Finish delivery", cut.Markup);
		Assert.Contains("Review changes", cut.Markup);
		Assert.Contains("Go to delivery", cut.Markup);
	}

	[Fact]
	public void JobOutcomeSummaryCard_PrefersMergedOutcomeOverPullRequestReady()
	{
		using var context = new BunitContext();

		var cut = context.Render<JobOutcomeSummaryCard>(parameters => parameters
			.Add(component => component.Status, JobStatus.Completed)
			.Add(component => component.PullRequestNumber, 42)
			.Add(component => component.PullRequestUrl, "https://github.com/octo-org/octo-repo/pull/42")
			.Add(component => component.MergedAt, DateTime.UtcNow));

		Assert.Contains("Changes merged into the target branch", cut.Markup);
		Assert.Contains("Delivery is complete", cut.Markup);
		Assert.DoesNotContain("PR #42 ready for review", cut.Markup);
	}

	[Fact]
	public void JobOutcomeSummaryCard_PrefersDeliveryStateOverRawChangedFileCount()
	{
		using var context = new BunitContext();

		var cut = context.Render<JobOutcomeSummaryCard>(parameters => parameters
			.Add(component => component.Status, JobStatus.Completed)
			.Add(component => component.ChangedFilesCount, 4)
			.Add(component => component.PullRequestNumber, 42)
			.Add(component => component.PullRequestUrl, "https://github.com/octo-org/octo-repo/pull/42")
			.Add(component => component.TargetBranch, "main"));

		Assert.Contains("PR #42 created", cut.Markup);
		Assert.Contains("PR #42 is ready for review targeting main.", cut.Markup);
		Assert.DoesNotContain("4 files were recorded for this run.", cut.Markup);
	}

	[Fact]
	public void JobOutcomeSummaryCard_ShowsSessionSummaryWhenAvailable()
	{
		using var context = new BunitContext();

		var cut = context.Render<JobOutcomeSummaryCard>(parameters => parameters
			.Add(component => component.Status, JobStatus.Completed)
			.Add(component => component.ChangedFilesCount, 0)
			.Add(component => component.SessionSummary, "Implemented the delivery summary.\nImproved the outcome guidance."));

		Assert.Contains("Work summary", cut.Markup);
		Assert.Contains("Implemented the delivery summary.", cut.Markup);
		Assert.Contains("Improved the outcome guidance.", cut.Markup);
	}

	[Fact]
	public void JobOutcomeHint_ShowsCompactVerificationFailureGuidance()
	{
		using var context = new BunitContext();

		var cut = context.Render<JobOutcomeHint>(parameters => parameters
			.Add(component => component.Status, JobStatus.Completed)
			.Add(component => component.ChangedFilesCount, 2)
			.Add(component => component.BuildVerified, false));

		Assert.Contains("Checks failed.", cut.Markup);
		Assert.Contains("Review the verification output before delivering these changes.", cut.Markup);
	}

	[Fact]
	public void JobOutcomeHint_ShowsVerificationMissingGuidanceWhenChecksWereExpected()
	{
		using var context = new BunitContext();

		var cut = context.Render<JobOutcomeHint>(parameters => parameters
			.Add(component => component.Status, JobStatus.Completed)
			.Add(component => component.ChangedFilesCount, 2)
			.Add(component => component.BuildVerificationEnabled, true));

		Assert.Contains("Checks missing.", cut.Markup);
		Assert.Contains("this run did not record it", cut.Markup);
	}

	[Fact]
	public void JobOutcomeHint_ShowsMergedGuidanceBeforePullRequestReady()
	{
		using var context = new BunitContext();

		var cut = context.Render<JobOutcomeHint>(parameters => parameters
			.Add(component => component.Status, JobStatus.Completed)
			.Add(component => component.PullRequestNumber, 42)
			.Add(component => component.PullRequestUrl, "https://github.com/octo-org/octo-repo/pull/42")
			.Add(component => component.MergedAt, DateTime.UtcNow));

		Assert.Contains("Merged.", cut.Markup);
		Assert.Contains("The changes are already on the target branch", cut.Markup);
		Assert.DoesNotContain("PR #42 ready.", cut.Markup);
	}

	[Fact]
	public void JobOutcomeHint_ShowsPushedBranchGuidanceBeforeCommitOnlyState()
	{
		using var context = new BunitContext();

		var cut = context.Render<JobOutcomeHint>(parameters => parameters
			.Add(component => component.Status, JobStatus.Completed)
			.Add(component => component.GitCommitHash, "0123456789abcdef")
			.Add(component => component.IsPushed, true));

		Assert.Contains("Branch pushed.", cut.Markup);
		Assert.Contains("The remote branch is updated", cut.Markup);
		Assert.DoesNotContain("Commit 0123456 created.", cut.Markup);
	}

	[Fact]
	public void JobListItem_ShowsCompactOutcomeHintForPullRequestReadyRuns()
	{
		using var context = new BunitContext();

		var cut = context.Render<JobListItem>(parameters => parameters
			.Add(component => component.Status, JobStatus.Completed.ToString())
			.Add(component => component.Prompt, "Ship the fix")
			.Add(component => component.TimeDisplay, "now")
			.Add(component => component.PullRequestNumber, 42)
			.Add(component => component.PullRequestUrl, "https://github.com/octo-org/octo-repo/pull/42"));

		Assert.Contains("PR #42 ready.", cut.Markup);
		Assert.Contains("Review it and merge when the changes are approved.", cut.Markup);
	}

	[Fact]
	public void JobListItem_SupportsKeyboardActivation()
	{
		using var context = new BunitContext();

		var clicked = false;
		var cut = context.Render<JobListItem>(parameters => parameters
			.Add(component => component.Status, JobStatus.Completed.ToString())
			.Add(component => component.Prompt, "Ship the fix")
			.Add(component => component.TimeDisplay, "now")
			.Add(component => component.OnClick, EventCallback.Factory.Create(this, () => clicked = true)));

		var item = cut.Find(".job-list-item");
		Assert.Equal("button", item.GetAttribute("role"));
		Assert.Equal("0", item.GetAttribute("tabindex"));

		item.KeyDown(new KeyboardEventArgs { Key = "Enter" });

		Assert.True(clicked);
	}

	[Fact]
	public void JobListItem_ShowsPushedOutcomeHintForRemoteBranchRuns()
	{
		using var context = new BunitContext();

		var cut = context.Render<JobListItem>(parameters => parameters
			.Add(component => component.Status, JobStatus.Completed.ToString())
			.Add(component => component.Prompt, "Ship the fix")
			.Add(component => component.TimeDisplay, "now")
			.Add(component => component.GitCommitHash, "0123456789abcdef")
			.Add(component => component.IsPushed, true));

		Assert.Contains("Pushed", cut.Markup);
		Assert.Contains("Branch pushed.", cut.Markup);
	}

	[Fact]
	public void JobListItem_ShowsMergedOutcomeHintWhenMergeWasRecorded()
	{
		using var context = new BunitContext();

		var cut = context.Render<JobListItem>(parameters => parameters
			.Add(component => component.Status, JobStatus.Completed.ToString())
			.Add(component => component.Prompt, "Ship the fix")
			.Add(component => component.TimeDisplay, "now")
			.Add(component => component.PullRequestNumber, 42)
			.Add(component => component.PullRequestUrl, "https://github.com/octo-org/octo-repo/pull/42")
			.Add(component => component.MergedAt, DateTime.UtcNow));

		Assert.Contains("Merged.", cut.Markup);
		Assert.Contains("The changes are already on the target branch", cut.Markup);
		Assert.DoesNotContain("PR #42 ready.", cut.Markup);
	}

	[Fact]
	public void JobOutcomeSummarySnippet_ShowsFirstSummaryLine()
	{
		using var context = new BunitContext();

		var cut = context.Render<JobOutcomeSummarySnippet>(parameters => parameters
			.Add(component => component.SessionSummary, "- Tightened the outcome UX across the job lists.\n- Added better delivery guidance."));

		Assert.Contains("Work summary:", cut.Markup);
		Assert.Contains("Tightened the outcome UX across the job lists.", cut.Markup);
		Assert.DoesNotContain("Added better delivery guidance.", cut.Markup);
	}

	[Fact]
	public void JobListItem_ShowsCompactWorkSummaryWhenSessionSummaryExists()
	{
		using var context = new BunitContext();

		var cut = context.Render<JobListItem>(parameters => parameters
			.Add(component => component.Status, JobStatus.Completed.ToString())
			.Add(component => component.Prompt, "Ship the fix")
			.Add(component => component.TimeDisplay, "now")
			.Add(component => component.SessionSummary, "Polished the delivery guidance for completed jobs.\nAdded mobile-safe list summaries."));

		Assert.Contains("Work summary:", cut.Markup);
		Assert.Contains("Polished the delivery guidance for completed jobs.", cut.Markup);
		Assert.DoesNotContain("Added mobile-safe list summaries.", cut.Markup);
	}

	[Fact]
	public void JobListItem_HidesProviderAndEnvironmentDetailsByDefault()
	{
		using var context = new BunitContext();

		var cut = context.Render<JobListItem>(parameters => parameters
			.Add(component => component.Status, JobStatus.Completed.ToString())
			.Add(component => component.Prompt, "Ship the fix")
			.Add(component => component.TimeDisplay, "now")
			.Add(component => component.ProviderName, "Copilot")
			.Add(component => component.ModelUsed, "gpt-5.4")
			.Add(component => component.PlanningProviderName, "Claude")
			.Add(component => component.PlanningModelUsed, "claude-sonnet-4")
			.Add(component => component.PlaywrightEnabled, true)
			.Add(component => component.EnvironmentCount, 2));

		Assert.DoesNotContain("Claude -&gt; Copilot", cut.Markup);
		Assert.DoesNotContain("gpt-5.4", cut.Markup);
		Assert.DoesNotContain("bi-browser-chrome", cut.Markup);
		Assert.DoesNotContain("bi-globe", cut.Markup);
	}

	[Fact]
	public void JobOutcomeSummaryCard_ShowsMergeBranchButtonWhenMergeIsAvailable()
	{
		using var context = new BunitContext();
		var mergeCalled = false;

		var cut = context.Render<JobOutcomeSummaryCard>(parameters => parameters
			.Add(component => component.Status, JobStatus.Completed)
			.Add(component => component.GitCommitHash, "abc1234567890")
			.Add(component => component.BranchName, "feature/new-thing")
			.Add(component => component.TargetBranch, "main")
			.Add(component => component.CanMergeBranch, true)
			.Add(component => component.OnMergeBranch, EventCallback.Factory.Create(this, () => mergeCalled = true)));

		Assert.Contains("Merge into main", cut.Markup);
		Assert.Contains("Merge into the target branch", cut.Markup);
		cut.Find("button.btn-success").Click();
		Assert.True(mergeCalled);
	}

	[Fact]
	public void JobOutcomeSummaryCard_HidesMergeBranchButtonWhenAlreadyMerged()
	{
		using var context = new BunitContext();

		var cut = context.Render<JobOutcomeSummaryCard>(parameters => parameters
			.Add(component => component.Status, JobStatus.Completed)
			.Add(component => component.GitCommitHash, "abc1234567890")
			.Add(component => component.BranchName, "feature/new-thing")
			.Add(component => component.TargetBranch, "main")
			.Add(component => component.CanMergeBranch, true)
			.Add(component => component.MergedAt, DateTime.UtcNow));

		Assert.DoesNotContain("Merge into main", cut.Markup);
		Assert.Contains("Delivery is complete", cut.Markup);
	}

	[Fact]
	public void JobOutcomeSummaryCard_ShowsReviewTranscriptActionForStalledRuns()
	{
		using var context = new BunitContext();

		var cut = context.Render<JobOutcomeSummaryCard>(parameters => parameters
			.Add(component => component.Status, JobStatus.Stalled)
			.Add(component => component.ReviewTranscriptHref, "#job-messages-section"));

		Assert.Contains("Run stalled before completion", cut.Markup);
		Assert.Contains("Review transcript", cut.Markup);
		Assert.Contains("#job-messages-section", cut.Markup);
	}

	[Fact]
	public void JobOutcomeBadges_ShowsShortCommitLabelForCommittedRuns()
	{
		using var context = new BunitContext();

		var cut = context.Render<JobOutcomeBadges>(parameters => parameters
			.Add(component => component.Status, JobStatus.Completed)
			.Add(component => component.GitCommitHash, "0123456789abcdef"));

		Assert.Contains("Commit 0123456", cut.Markup);
	}

	[Fact]
	public void JobOutcomeBadges_ShowsPushedBadgeForRemoteBranchState()
	{
		using var context = new BunitContext();

		var cut = context.Render<JobOutcomeBadges>(parameters => parameters
			.Add(component => component.Status, JobStatus.Completed)
			.Add(component => component.GitCommitHash, "0123456789abcdef")
			.Add(component => component.IsPushed, true));

		Assert.Contains("Pushed", cut.Markup);
		Assert.Contains("Commit 0123456", cut.Markup);
	}

	[Fact]
	public void JobOutcomeSummaryCard_ShowsPushedBranchOutcomeBeforeCommitOnlyState()
	{
		using var context = new BunitContext();

		var cut = context.Render<JobOutcomeSummaryCard>(parameters => parameters
			.Add(component => component.Status, JobStatus.Completed)
			.Add(component => component.GitCommitHash, "0123456789abcdef")
			.Add(component => component.IsPushed, true));

		Assert.Contains("Changes pushed to the remote branch", cut.Markup);
		Assert.Contains("Changes pushed remotely", cut.Markup);
		Assert.DoesNotContain("Commit recorded", cut.Markup);
	}
}
