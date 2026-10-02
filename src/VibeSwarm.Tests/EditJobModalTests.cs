using Bunit;
using Microsoft.Extensions.DependencyInjection;
using VibeSwarm.Client.Components.Jobs;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

public sealed class EditJobModalTests
{
	[Fact]
	public void WarnsWhileQueueRuns_WithTheProjectsOtherJobs()
	{
		var queue = new FakeQueueControl { RunningJobs = 1, QueuedJobs = 3 };
		using var context = CreateContext(queue);

		var cut = context.Render<EditJobModal>(parameters => parameters
			.Add(modal => modal.IsVisible, true)
			.Add(modal => modal.Job, CreateJob(JobStatus.New)));

		Assert.Contains("The queue is running, so this job could start before you save.", cut.Markup);
		Assert.Contains("This project has 1 running and 2 other queued jobs.", cut.Markup);
		Assert.Contains("Pause this project while editing", cut.Markup);
		Assert.False(cut.Find("#edit-job-prompt").HasAttribute("disabled"));
	}

	[Fact]
	public void DoesNotWarn_WhenTheWholeQueueIsPaused()
	{
		using var context = CreateContext(new FakeQueueControl { IsQueuePaused = true, QueuedJobs = 1 });

		var cut = context.Render<EditJobModal>(parameters => parameters
			.Add(modal => modal.IsVisible, true)
			.Add(modal => modal.Job, CreateJob(JobStatus.New)));

		Assert.DoesNotContain("could start before you save", cut.Markup);
		Assert.Contains("The queue is paused", cut.Markup);
	}

	[Fact]
	public void PausingHoldsTheProject_AndClosingReleasesIt()
	{
		var queue = new FakeQueueControl { QueuedJobs = 1 };
		using var context = CreateContext(queue);
		var job = CreateJob(JobStatus.New);

		var cut = context.Render<EditJobModal>(parameters => parameters
			.Add(modal => modal.IsVisible, true)
			.Add(modal => modal.Job, job));

		cut.FindAll("button").First(button => button.TextContent.Contains("Pause this project while editing")).Click();

		Assert.Equal([job.ProjectId], queue.PausedProjects);
		Assert.Contains("paused while you edit", cut.Markup);

		cut.FindAll("button").First(button => button.TextContent.Contains("Cancel")).Click();

		Assert.Equal([job.ProjectId], queue.ResumedProjects);
	}

	[Fact]
	public void LocksOnceTheJobStarts_AndOffersToStopIt()
	{
		using var context = CreateContext(new FakeQueueControl { QueuedJobs = 1 });
		var job = CreateJob(JobStatus.New);
		var stopped = false;

		var cut = context.Render<EditJobModal>(parameters => parameters
			.Add(modal => modal.IsVisible, true)
			.Add(modal => modal.Job, job)
			.Add(modal => modal.OnStop, () => stopped = true));

		job.Status = JobStatus.Processing;
		cut.Render(parameters => parameters.Add(modal => modal.Job, job));

		Assert.Contains("This job has started, so its prompt can no longer be changed.", cut.Markup);
		Assert.True(cut.Find("#edit-job-prompt").HasAttribute("disabled"));
		Assert.True(cut.FindAll("button").First(button => button.TextContent.Contains("Save")).HasAttribute("disabled"));

		cut.FindAll("button").First(button => button.TextContent.Contains("Stop job")).Click();

		Assert.True(stopped);
	}

	[Fact]
	public void SavingSendsTheEditedPrompt()
	{
		var jobService = new FakeJobService();
		using var context = CreateContext(new FakeQueueControl(), jobService);
		var job = CreateJob(JobStatus.New);
		string? savedPrompt = null;

		var cut = context.Render<EditJobModal>(parameters => parameters
			.Add(modal => modal.IsVisible, true)
			.Add(modal => modal.Job, job)
			.Add(modal => modal.OnSaved, prompt => savedPrompt = prompt));

		cut.Find("#edit-job-prompt").Input("Tightened prompt");
		cut.FindAll("button").First(button => button.TextContent.Contains("Save")).Click();

		Assert.Equal((job.Id, "Tightened prompt"), jobService.LastUpdate);
		Assert.Equal("Tightened prompt", savedPrompt);
	}

	private static Job CreateJob(JobStatus status) => new()
	{
		Id = Guid.NewGuid(),
		ProjectId = Guid.NewGuid(),
		ProviderId = Guid.NewGuid(),
		GoalPrompt = "Original prompt",
		Title = "Original prompt",
		Status = status
	};

	private static BunitContext CreateContext(FakeQueueControl queueControl, FakeJobService? jobService = null)
	{
		var context = new BunitContext();
		context.Services.AddLogging();
		context.Services.AddSingleton<IJobQueueControlService>(queueControl);
		context.Services.AddSingleton<IJobService>(jobService ?? new FakeJobService());
		context.JSInterop.Mode = JSRuntimeMode.Loose;
		return context;
	}

	private sealed class FakeJobService : FakeJobServiceBase
	{
		public (Guid JobId, string Prompt)? LastUpdate { get; private set; }

		public override Task<bool> UpdateJobPromptAsync(Guid id, string newPrompt, CancellationToken cancellationToken = default)
		{
			LastUpdate = (id, newPrompt);
			return Task.FromResult(true);
		}
	}

	private sealed class FakeQueueControl : IJobQueueControlService
	{
		public bool IsQueuePaused { get; init; }
		public int RunningJobs { get; init; }
		public int QueuedJobs { get; init; }
		public List<Guid> PausedProjects { get; } = [];
		public List<Guid> ResumedProjects { get; } = [];

		public Task<JobQueueState> GetStateAsync(CancellationToken cancellationToken = default)
			=> Task.FromResult(new JobQueueState { IsPaused = IsQueuePaused });

		public Task<JobQueueState> PauseAsync(string? reason = null, bool cancelRunningJobs = false, CancellationToken cancellationToken = default)
			=> throw new NotSupportedException();

		public Task<JobQueueState> ResumeAsync(CancellationToken cancellationToken = default)
			=> throw new NotSupportedException();

		public Task<ProjectQueueState> GetProjectStateAsync(Guid projectId, CancellationToken cancellationToken = default)
			=> Task.FromResult(BuildState(projectId, isProjectPaused: false));

		public Task<ProjectQueueState> PauseProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
		{
			PausedProjects.Add(projectId);
			return Task.FromResult(BuildState(projectId, isProjectPaused: true));
		}

		public Task<ProjectQueueState> ResumeProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
		{
			ResumedProjects.Add(projectId);
			return Task.FromResult(BuildState(projectId, isProjectPaused: false));
		}

		private ProjectQueueState BuildState(Guid projectId, bool isProjectPaused) => new()
		{
			ProjectId = projectId,
			IsQueuePaused = IsQueuePaused,
			IsProjectPaused = isProjectPaused,
			RunningJobs = RunningJobs,
			QueuedJobs = QueuedJobs
		};
	}
}
