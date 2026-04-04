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
			.Add(component => component.PullRequestUrl, "https://github.com/octo-org/octo-repo/pull/42"));

		Assert.Contains("3 files changed", cut.Markup);
		Assert.Contains("Build verified", cut.Markup);
		Assert.Contains("PR ready", cut.Markup);
	}

	[Fact]
	public void JobOutcomeBadges_ShowsNeedsReviewWhenFailedChangesRemain()
	{
		using var context = new BunitContext();

		var cut = context.Render<JobOutcomeBadges>(parameters => parameters
			.Add(component => component.Status, JobStatus.Failed)
			.Add(component => component.ChangedFilesCount, 2));

		Assert.Contains("2 files changed", cut.Markup);
		Assert.Contains("Needs review", cut.Markup);
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
		Assert.Contains("Build failed", cut.Markup);
		Assert.Contains("Review the verification failure", cut.Markup);
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

		Assert.Contains("Verification was enabled", cut.Markup);
		Assert.Contains("dotnet build", cut.Markup);
		Assert.Contains("dotnet test", cut.Markup);
		Assert.Contains("Ready for delivery", cut.Markup);
		Assert.Contains("Pull request flow", cut.Markup);
		Assert.Contains("Finish delivery", cut.Markup);
		Assert.Contains("Review changes", cut.Markup);
		Assert.Contains("Go to delivery", cut.Markup);
	}

	[Fact]
	public void JobOutcomeHint_ShowsCompactVerificationFailureGuidance()
	{
		using var context = new BunitContext();

		var cut = context.Render<JobOutcomeHint>(parameters => parameters
			.Add(component => component.Status, JobStatus.Completed)
			.Add(component => component.ChangedFilesCount, 2)
			.Add(component => component.BuildVerified, false));

		Assert.Contains("Verification failed.", cut.Markup);
		Assert.Contains("Review the output before delivering these changes.", cut.Markup);
	}

	[Fact]
	public void JobListItem_ShowsCompactOutcomeHintForPullRequestReadyRuns()
	{
		using var context = new BunitContext();

		var cut = context.Render<JobListItem>(parameters => parameters
			.Add(component => component.Status, JobStatus.Completed.ToString())
			.Add(component => component.Prompt, "Ship the fix")
			.Add(component => component.TimeDisplay, "now")
			.Add(component => component.PullRequestUrl, "https://github.com/octo-org/octo-repo/pull/42"));

		Assert.Contains("PR ready.", cut.Markup);
		Assert.Contains("Review and merge it when the changes are approved.", cut.Markup);
	}
}
