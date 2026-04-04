using Bunit;
using VibeSwarm.Client.Components.Jobs;
using VibeSwarm.Shared.Data;

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
		Assert.Contains("Build failed", cut.Markup);
		Assert.Contains("Show verification output", cut.Markup);
		Assert.Contains("dotnet test failed", cut.Markup);
	}
}
