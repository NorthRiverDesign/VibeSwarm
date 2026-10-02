using Bunit;
using VibeSwarm.Client.Components.Scheduler;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Utilities;

namespace VibeSwarm.Tests;

public sealed class ScheduleRowTests
{
	[Fact]
	public void OpenedRow_ShowsRunnerCadenceLastRunAndActions()
	{
		using var context = new BunitContext();
		var lastRunAtUtc = DateTime.UtcNow.AddMinutes(-37);
		var schedule = CreateJobSchedule();
		schedule.LastRunAtUtc = lastRunAtUtc;
		JobSchedule? paused = null;

		var cut = context.Render<ScheduleRow>(parameters => parameters
			.Add(component => component.Schedule, schedule)
			.Add(component => component.OnToggle, s => paused = s));

		Assert.DoesNotContain("Copilot", cut.Markup);

		cut.Find("button[aria-expanded]").Click();

		Assert.Contains("Copilot", cut.Markup);
		Assert.Contains("Daily at 09:00", cut.Markup);
		Assert.Contains(lastRunAtUtc.FormatRelativeToNow(), cut.Markup);
		Assert.Contains("Edit", cut.Markup);
		Assert.Contains("Delete", cut.Markup);

		cut.FindAll("button").Single(button => button.TextContent.Trim() == "Pause").Click();
		Assert.Same(schedule, paused);
	}

	[Fact]
	public void AgentSchedule_NamesTheAgent()
	{
		using var context = new BunitContext();
		var schedule = CreateJobSchedule();
		schedule.ExecutionTarget = JobScheduleExecutionTarget.Agent;
		schedule.Agent = new Agent { Id = Guid.NewGuid(), Name = "Security Reviewer" };

		var cut = context.Render<ScheduleRow>(parameters => parameters.Add(component => component.Schedule, schedule));
		cut.Find("button[aria-expanded]").Click();

		Assert.Contains("Security Reviewer", cut.Markup);
		Assert.DoesNotContain("Unknown agent", cut.Markup);
	}

	[Fact]
	public void IdeaSchedule_NamesTheInferenceProvider()
	{
		using var context = new BunitContext();
		var schedule = CreateJobSchedule();
		schedule.ScheduleType = JobScheduleType.GenerateIdeas;
		schedule.IdeaCount = 3;
		schedule.InferenceProvider = new InferenceProvider { Id = Guid.NewGuid(), Name = "Local Ollama" };

		var cut = context.Render<ScheduleRow>(parameters => parameters.Add(component => component.Schedule, schedule));

		Assert.Contains("Generate 3 ideas", cut.Markup);

		cut.Find("button[aria-expanded]").Click();

		Assert.Contains("Local Ollama", cut.Markup);
	}

	[Fact]
	public void FailedLastRun_ReplacesTheSummaryAndRunningJobShows()
	{
		using var context = new BunitContext();
		var schedule = CreateJobSchedule();
		schedule.LastError = "Provider unavailable";

		var cut = context.Render<ScheduleRow>(parameters => parameters
			.Add(component => component.Schedule, schedule)
			.Add(component => component.ActiveJob, new JobSummary { Id = Guid.NewGuid(), CurrentActivity = "Running tests" }));

		Assert.Contains("Last run failed", cut.Markup);
		Assert.Contains("Running", cut.Markup);
	}

	private static JobSchedule CreateJobSchedule() => new()
	{
		Id = Guid.NewGuid(),
		Prompt = "update dependencies, check security issues",
		Frequency = JobScheduleFrequency.Daily,
		HourUtc = 9,
		MinuteUtc = 0,
		IsEnabled = true,
		NextRunAtUtc = DateTime.UtcNow.AddHours(2),
		Project = new Project { Id = Guid.NewGuid(), Name = "Repo", WorkingPath = "/tmp/repo" },
		Provider = new Provider { Id = Guid.NewGuid(), Name = "Copilot", Type = ProviderType.Copilot, IsEnabled = true }
	};
}
