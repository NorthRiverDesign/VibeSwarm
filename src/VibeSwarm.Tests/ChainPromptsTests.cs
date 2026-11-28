using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

public sealed class ChainPromptsTests
{
	[Fact]
	public void ParsePlan_ReadsTheBriefAndEachStep()
	{
		var plan = ChainPrompts.ParsePlan("""
			Outcome: calc.py gains divide and power.
			Acceptance: both handle zero.

			## STEP: Add divide
			Do: Add divide(a, b) to calc.py after multiply.
			It returns a / b.
			Done when: divide(6, 3) is 2 and the import check passes.

			## Step 2: **Add power**
			Do: Add power(base, exponent).
			Done when: power(2, 3) is 8.
			""");

		Assert.StartsWith("Outcome: calc.py gains divide and power.", plan.Brief);
		Assert.DoesNotContain("STEP", plan.Brief);
		Assert.Equal(["Add divide", "Add power"], plan.Steps.Select(step => step.Title));
		Assert.Equal("Add divide(a, b) to calc.py after multiply.\nIt returns a / b.", plan.Steps[0].Instructions.ReplaceLineEndings("\n"));
		Assert.Equal("divide(6, 3) is 2 and the import check passes.", plan.Steps[0].DoneWhen);
		Assert.Equal("power(2, 3) is 8.", plan.Steps[1].DoneWhen);
	}

	[Fact]
	public void ParsePlan_WithNoStepHeadings_IsOneStepHoldingTheWholePlan()
	{
		var plan = ChainPrompts.ParsePlan("Just add the function and a test.", goal: "Add a divide function to calc.py.\nKeep it small.");

		var step = Assert.Single(plan.Steps);
		Assert.Equal("Add a divide function to calc.py.", step.Title);
		Assert.Equal("Just add the function and a test.", plan.Brief);
	}

	[Fact]
	public void ParsePlan_KeepsOnlyTheFirstStepsOfAnOverlongList_AndAStepWithoutMarkersKeepsItsText()
	{
		var reply = "Brief.\n\n" + string.Join("\n\n", Enumerable.Range(1, 11).Select(number => $"## STEP: Part {number}\nBuild part {number}."));

		var plan = ChainPrompts.ParsePlan(reply);

		Assert.Equal(ChainPrompts.MaxPlannedSteps, plan.Steps.Count);
		Assert.Equal("Part 8", plan.Steps[^1].Title);
		Assert.Equal("Build part 1.", plan.Steps[0].Instructions);
		Assert.Null(plan.Steps[0].DoneWhen);
	}

	[Fact]
	public void StepSection_ShowsWhereTheStepSits_AndWhatEarlierStepsLeft()
	{
		var done = new JobStep { Order = 1, Title = "Add divide", Status = JobStepStatus.Done, Summary = "divide is in calc.py; zero returns None." };
		var skipped = new JobStep { Order = 2, Title = "Dropped", Status = JobStepStatus.Skipped };
		var mine = new JobStep { Order = 3, Title = "Add power", Instructions = "Add power(base, exponent).", DoneWhen = "power(2, 3) is 8.", Status = JobStepStatus.Running };
		var later = new JobStep { Order = 4, Title = "Document both" };

		var section = ChainPrompts.BuildStepSection("Outcome: two functions.", [later, mine, skipped, done], mine);

		Assert.Contains("<brief>", section);
		Assert.Contains("1. [done] Add divide", section);
		Assert.Contains("Note from its builder: divide is in calc.py; zero returns None.", section);
		Assert.Contains("2. [yours] Add power", section);
		Assert.Contains("3. [later] Document both", section);
		Assert.DoesNotContain("Dropped", section);
		Assert.Contains("<your_step number=\"2\" of=\"3\">", section);
		Assert.Contains("Done when: power(2, 3) is 8.", section);
		Assert.Contains("## Handoff", section);
	}

	[Fact]
	public void ExtractHandoff_TakesTheNoteUnderItsHeading_OrTheClosingLines()
	{
		Assert.Equal("Added divide. Zero returns None.", ChainPrompts.ExtractHandoff("Did the work.\n\n## Handoff\nAdded divide. Zero returns None."));
		Assert.Equal("Added divide.", ChainPrompts.ExtractHandoff("Added divide."));
		Assert.Null(ChainPrompts.ExtractHandoff("  "));
		Assert.StartsWith("…", ChainPrompts.ExtractHandoff(new string('x', 700)));
	}

	[Fact]
	public void PlanPrompt_AsksForABriefAndStepsInTheFormatTheParserReads()
	{
		var prompt = ChainPrompts.BuildPlanPrompt("Add a divide function.");

		Assert.Contains("Add a divide function.", prompt);
		Assert.Contains("## STEP: <short title>", prompt);
		Assert.Contains("Done when:", prompt);
		Assert.Contains("building and its tests passing", prompt);
		// The Lead names the whole job once, so the commit isn't titled after its last step.
		Assert.Contains("<commit-summary>", prompt);
	}
}
