using Bunit;
using VibeSwarm.Client.Components.Jobs;
using VibeSwarm.Shared.Data;

namespace VibeSwarm.Tests;

public sealed class JobStatusSectionTests
{
	[Fact]
	public void CompletedJobWhoseChecksFailed_SaysItWasNotCommitted_AndShowsTheEndOfTheCheckOutput()
	{
		using var context = new BunitContext();
		var testOutput = string.Join('\n', Enumerable.Range(1, 120).Select(line => $"test line {line}"))
			+ "\n  Failed VibeSwarm.Tests.SomeTimingTest [43 ms]\nFailed!  - Failed:     1, Passed:  1135";

		var cut = context.Render<JobStatusSection>(parameters => parameters
			.Add(section => section.Job, new Job
			{
				Status = JobStatus.Completed,
				BuildVerified = false,
				BuildOutput = testOutput,
				ChangedFilesCount = 10
			}));

		var banner = cut.Find(".alert");
		Assert.Contains("theme-warning", banner.ClassName);
		Assert.Contains("Checks failed, not committed", banner.TextContent);
		Assert.Contains("left uncommitted", banner.TextContent);

		var output = cut.Find("details pre").TextContent;
		Assert.Contains("Failed VibeSwarm.Tests.SomeTimingTest", output);
		// Only the last 80 of the 122 lines.
		Assert.Contains("test line 43\n", output);
		Assert.DoesNotContain("test line 42\n", output);

		var committedStep = cut.FindAll(".stepper-item")[2];
		Assert.Contains("stepper-item-failed", committedStep.ClassName);
		Assert.Contains("Checks failed", committedStep.TextContent);
	}

	[Fact]
	public void CompletedJobThatPassedItsChecks_KeepsTheSuccessBanner()
	{
		using var context = new BunitContext();

		var cut = context.Render<JobStatusSection>(parameters => parameters
			.Add(section => section.Job, new Job
			{
				Status = JobStatus.Completed,
				BuildVerified = true,
				BuildOutput = "Passed!",
				GitCommitHash = "a4dc0d7f0000000000000000000000000000000"
			}));

		var banner = cut.Find(".alert");
		Assert.Contains("theme-success", banner.ClassName);
		Assert.Contains("Job completed", banner.TextContent);
		Assert.Empty(cut.FindAll("details"));
	}
}
