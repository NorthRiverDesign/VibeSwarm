using Bunit;
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
}
