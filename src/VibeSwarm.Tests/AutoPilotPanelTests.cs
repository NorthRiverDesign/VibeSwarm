using Bunit;
using Microsoft.Extensions.DependencyInjection;
using VibeSwarm.Client.Components.Projects;
using VibeSwarm.Client.Services;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

public sealed class AutoPilotPanelTests
{
	private static readonly Guid ProjectId = Guid.NewGuid();

	[Fact]
	public void Idle_FoldsTheTuningAwayAndStartsWithThePolishCadence()
	{
		var service = new FakeAutoPilotService(null);
		using var context = CreateContext(service);
		var cut = context.Render<AutoPilotPanel>(parameters => parameters.Add(p => p.ProjectId, ProjectId));

		cut.WaitForAssertion(() => Assert.Contains("Start Auto-Pilot", cut.Markup));
		Assert.Empty(cut.FindAll("#ap-polish-every"));

		cut.FindAll("button").Single(b => b.TextContent.Contains("More options")).Click();
		Assert.Equal("5", cut.Find("#ap-polish-every").GetAttribute("value"));
		cut.Find("#ap-polish-every").Change("3");
		cut.FindAll("button").Single(b => b.TextContent.Contains("Start Auto-Pilot")).Click();

		Assert.Equal(3, service.StartedWith?.PolishEveryIterations);
	}

	[Fact]
	public void Idle_TakesIdeasFromAnyInferenceProviderWhenOneIsEnabled()
	{
		var service = new FakeAutoPilotService(null);
		using var context = CreateContext(service);
		var cut = context.Render<AutoPilotPanel>(parameters => parameters
			.Add(p => p.ProjectId, ProjectId)
			.Add(p => p.AvailableInferenceProviders, [new InferenceProvider { Id = Guid.NewGuid(), Name = "Grok", IsEnabled = true }]));

		cut.WaitForAssertion(() => Assert.Contains("Start Auto-Pilot", cut.Markup));
		cut.FindAll("button").Single(b => b.TextContent.Contains("Start Auto-Pilot")).Click();

		Assert.True(service.StartedWith?.UseInferenceForIdeas);
		Assert.Null(service.StartedWith?.InferenceProviderId);
	}

	[Fact]
	public void Idle_AsksTheCodingAgentForIdeasWithoutInference()
	{
		var service = new FakeAutoPilotService(null);
		using var context = CreateContext(service);
		var cut = context.Render<AutoPilotPanel>(parameters => parameters.Add(p => p.ProjectId, ProjectId));

		cut.WaitForAssertion(() => Assert.Contains("Start Auto-Pilot", cut.Markup));
		cut.FindAll("button").Single(b => b.TextContent.Contains("Start Auto-Pilot")).Click();

		Assert.False(service.StartedWith?.UseInferenceForIdeas);
	}

	[Fact]
	public void Idle_PickingAnInferenceProviderSendsIt()
	{
		var service = new FakeAutoPilotService(null);
		using var context = CreateContext(service);
		var grok = new InferenceProvider { Id = Guid.NewGuid(), Name = "Grok", IsEnabled = true };
		var cut = context.Render<AutoPilotPanel>(parameters => parameters
			.Add(p => p.ProjectId, ProjectId)
			.Add(p => p.AvailableInferenceProviders, [grok]));

		cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#ap-idea-source")));
		cut.Find("#ap-idea-source").Change(grok.Id.ToString());
		cut.FindAll("button").Single(b => b.TextContent.Contains("Start Auto-Pilot")).Click();

		Assert.Equal(grok.Id, service.StartedWith?.InferenceProviderId);
		Assert.True(service.StartedWith?.UseInferenceForIdeas);
	}

	[Fact]
	public void Idle_SaysWhenAnAgentsQuestionWouldWaitForTheUser()
	{
		using var context = CreateContext(null, inferenceAnswersQuestions: false);
		var cut = context.Render<AutoPilotPanel>(parameters => parameters.Add(p => p.ProjectId, ProjectId));

		cut.WaitForAssertion(() => Assert.Contains("the job waits for you", cut.Markup));
	}

	[Fact]
	public void Running_ShowsWhatTheLoopIsWaitingForAndWhenItResumes()
	{
		using var context = CreateContext(new FakeAutoPilotService(new IterationLoop
		{
			ProjectId = ProjectId,
			Status = IterationLoopStatus.Running,
			StatusMessage = "Waiting for usage to reset: Claude reached its usage limit",
			NextIterationAt = DateTime.UtcNow.AddHours(1),
			PolishEveryIterations = 5,
			IterationsSinceLastPolish = 4
		}));

		var cut = context.Render<AutoPilotPanel>(parameters => parameters.Add(p => p.ProjectId, ProjectId));

		cut.WaitForAssertion(() => Assert.Contains("Claude reached its usage limit", cut.Markup));
		Assert.Contains("Resumes", cut.Markup);
	}

	[Fact]
	public void Running_ShowsWhenThePolishPassComes()
	{
		using var context = CreateContext(new FakeAutoPilotService(new IterationLoop
		{
			ProjectId = ProjectId,
			Status = IterationLoopStatus.Running,
			PolishEveryIterations = 5,
			IterationsSinceLastPolish = 4
		}));

		var cut = context.Render<AutoPilotPanel>(parameters => parameters.Add(p => p.ProjectId, ProjectId));

		cut.WaitForAssertion(() => Assert.Contains("Polish pass after 1 more change", cut.Markup));
	}

	[Fact]
	public void Running_LeavesOutAPolishPassTheRunEndsBefore()
	{
		using var context = CreateContext(new FakeAutoPilotService(new IterationLoop
		{
			ProjectId = ProjectId,
			Status = IterationLoopStatus.Running,
			MaxIterations = 2,
			CompletedIterations = 1,
			PolishEveryIterations = 5,
			IterationsSinceLastPolish = 1
		}));

		var cut = context.Render<AutoPilotPanel>(parameters => parameters.Add(p => p.ProjectId, ProjectId));

		cut.WaitForAssertion(() => Assert.Contains("1 of 2 changes", cut.Markup));
		Assert.DoesNotContain("Polish pass", cut.Markup);
	}

	[Fact]
	public async Task Running_ShowsTheJobRunningOnceThePageReportsIt()
	{
		var jobId = Guid.NewGuid();
		using var context = CreateContext(new FakeAutoPilotService(new IterationLoop
		{
			ProjectId = ProjectId,
			Status = IterationLoopStatus.Running,
			CurrentJobId = jobId,
			CurrentJobTitle = "Add CSV export",
			CurrentJobStatus = JobStatus.New
		}));
		var cut = context.Render<AutoPilotPanel>(parameters => parameters.Add(p => p.ProjectId, ProjectId));
		cut.WaitForAssertion(() => Assert.Contains("Queued", cut.Markup));

		await cut.InvokeAsync(() => cut.Instance.ShowStatus(new IterationLoop
		{
			ProjectId = ProjectId,
			Status = IterationLoopStatus.Running,
			CurrentJobId = jobId,
			CurrentJobTitle = "Add CSV export",
			CurrentJobStatus = JobStatus.Processing
		}));

		Assert.DoesNotContain("Queued", cut.Markup);
		Assert.Contains("Running", cut.Find("a[href='/jobs/view/" + jobId + "']").TextContent);
		Assert.DoesNotContain("Loading auto-pilot status", cut.Markup);
	}

	[Fact]
	public void Running_StopAsksFirst()
	{
		var service = new FakeAutoPilotService(new IterationLoop { ProjectId = ProjectId, Status = IterationLoopStatus.Running });
		using var context = CreateContext(service);
		var cut = context.Render<AutoPilotPanel>(parameters => parameters.Add(p => p.ProjectId, ProjectId));

		cut.WaitForAssertion(() => Assert.Contains("Stop Auto-Pilot", cut.Markup));
		cut.FindAll("button").Single(b => b.TextContent.Trim() == "Stop Auto-Pilot").Click();

		Assert.False(service.Stopped);
		Assert.Contains("Nothing new will start", cut.Markup);
	}

	[Fact]
	public void Running_NamesTheCurrentJobAndFlagsAQuestion()
	{
		using var context = CreateContext(new FakeAutoPilotService(new IterationLoop
		{
			ProjectId = ProjectId,
			Status = IterationLoopStatus.Running,
			CurrentJobId = Guid.NewGuid(),
			CurrentJobTitle = "Add CSV export",
			CurrentJobStatus = JobStatus.Paused
		}));

		var cut = context.Render<AutoPilotPanel>(parameters => parameters.Add(p => p.ProjectId, ProjectId));

		cut.WaitForAssertion(() => Assert.Contains("Add CSV export", cut.Markup));
		Assert.Contains("Waiting for your answer", cut.Markup);
	}

	private static BunitContext CreateContext(FakeAutoPilotService? service, bool inferenceAnswersQuestions = true)
	{
		var context = new BunitContext();
		context.JSInterop.Mode = JSRuntimeMode.Loose;
		context.Services.AddSingleton<IAutoPilotService>(service ?? new FakeAutoPilotService(null));
		context.Services.AddSingleton<ISettingsService>(new FakeSettingsService(inferenceAnswersQuestions));
		context.Services.AddSingleton<NotificationService>();
		return context;
	}

	private sealed class FakeSettingsService(bool inferenceAnswersQuestions) : ISettingsService
	{
		public Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
			Task.FromResult(new AppSettings { InferenceAnswersAgentQuestions = inferenceAnswersQuestions });

		public Task<AppSettings> UpdateSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<string?> GetDefaultProjectsDirectoryAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}

	private sealed class FakeAutoPilotService(IterationLoop? loop) : IAutoPilotService
	{
		public AutoPilotConfig? StartedWith { get; private set; }

		public Task<IterationLoop> StartAsync(Guid projectId, AutoPilotConfig config, CancellationToken cancellationToken = default)
		{
			StartedWith = config;
			return Task.FromResult(new IterationLoop { ProjectId = projectId, Status = IterationLoopStatus.Running });
		}

		public bool Stopped { get; private set; }

		public Task StopAsync(Guid projectId, CancellationToken cancellationToken = default)
		{
			Stopped = true;
			return Task.CompletedTask;
		}

		public Task PauseAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.CompletedTask;
		public Task ResumeAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.CompletedTask;
		public Task<IterationLoop?> GetStatusAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(loop);
		public Task<List<IterationLoop>> GetHistoryAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(new List<IterationLoop>());
		public Task<IterationLoop> UpdateConfigAsync(Guid projectId, AutoPilotConfig config, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}
}
