using Bunit;
using Microsoft.Extensions.DependencyInjection;
using VibeSwarm.Client.Components.Jobs;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

public sealed class JobStepsSectionTests
{
	[Fact]
	public void Steps_ShowWhereEachStands_AndOpenToWhatItDid()
	{
		using var context = new BunitContext();
		var jobId = Guid.NewGuid();
		context.Services.AddSingleton<IJobService>(new StepsJobService(
			new JobStep { Order = 1, Title = "Add divide", Instructions = "Add divide(a, b).", DoneWhen = "divide(6, 3) is 2.", Status = JobStepStatus.Done, Summary = "divide is in calc.py.", ReviewStatus = JobReviewStatus.Approved, ReviewRounds = 1 },
			new JobStep { Order = 2, Title = "Add power", Instructions = "Add power.", Status = JobStepStatus.Running },
			new JobStep { Order = 3, Title = "Dropped", Instructions = "Not needed.", Status = JobStepStatus.Skipped },
			new JobStep { Order = 4, Title = "Document both", Instructions = "Write the docs.", Source = JobStepSource.Check }));

		var cut = context.Render<JobStepsSection>(parameters => parameters.Add(section => section.JobId, jobId));

		Assert.Contains("1 of 3 done", cut.Markup);
		var rows = cut.FindAll("button[aria-expanded]");
		Assert.Equal(["Add divide", "Add power", "Dropped", "Document both"], rows.Select(row => row.QuerySelector(".fw-medium, .fw-semibold")!.TextContent.Trim()));
		Assert.Contains("divide is in calc.py.", rows[0].TextContent);
		Assert.Contains("Building now", rows[1].TextContent);
		Assert.NotNull(rows[1].QuerySelector(".spinner-border"));
		Assert.Contains("Skipped", rows[2].TextContent);
		Assert.Contains("added after the Lead's check", rows[3].TextContent);

		rows[0].Click();
		Assert.Contains("Add divide(a, b).", cut.Markup);
		Assert.Contains("divide(6, 3) is 2.", cut.Markup);
		Assert.Contains("Approved after 1 fix", cut.Markup);
	}

	[Fact]
	public void AJobBuiltInOneGo_ShowsNoStepsSection()
	{
		using var context = new BunitContext();
		context.Services.AddSingleton<IJobService>(new StepsJobService());

		var cut = context.Render<JobStepsSection>(parameters => parameters.Add(section => section.JobId, Guid.NewGuid()));

		Assert.Empty(cut.FindAll("section"));
	}

	private sealed class StepsJobService(params JobStep[] steps) : FakeJobServiceBase
	{
		public override Task<IReadOnlyList<JobStep>> GetStepsAsync(Guid jobId, CancellationToken cancellationToken = default)
			=> Task.FromResult<IReadOnlyList<JobStep>>(steps);
	}
}
