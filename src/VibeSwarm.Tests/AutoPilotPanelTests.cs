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
	public void Idle_OffersThePolishCadenceAndAlwaysCommits()
	{
		using var context = CreateContext(null);

		var cut = context.Render<AutoPilotPanel>(parameters => parameters.Add(p => p.ProjectId, ProjectId));

		cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#ap-polish-every")));
		Assert.Equal("5", cut.Find("#ap-polish-every").GetAttribute("value"));
		Assert.Empty(cut.FindAll("#ap-autocommit"));
		Assert.Contains("Every change is committed", cut.Markup);
	}

	[Fact]
	public void Idle_StartsTheLoopWithThePolishCadence()
	{
		var service = new FakeAutoPilotService(null);
		using var context = CreateContext(service);
		var cut = context.Render<AutoPilotPanel>(parameters => parameters.Add(p => p.ProjectId, ProjectId));

		cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#ap-polish-every")));
		cut.Find("#ap-polish-every").Change("3");
		cut.FindAll("button").Single(b => b.TextContent.Contains("Start Auto-Pilot")).Click();

		Assert.Equal(3, service.StartedWith?.PolishEveryIterations);
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
		Assert.Contains("Polish pass after 1 more change", cut.Markup);
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

	private static BunitContext CreateContext(FakeAutoPilotService? service)
	{
		var context = new BunitContext();
		context.JSInterop.Mode = JSRuntimeMode.Loose;
		context.Services.AddSingleton<IAutoPilotService>(service ?? new FakeAutoPilotService(null));
		context.Services.AddSingleton<NotificationService>();
		return context;
	}

	private sealed class FakeAutoPilotService(IterationLoop? loop) : IAutoPilotService
	{
		public AutoPilotConfig? StartedWith { get; private set; }

		public Task<IterationLoop> StartAsync(Guid projectId, AutoPilotConfig config, CancellationToken cancellationToken = default)
		{
			StartedWith = config;
			return Task.FromResult(new IterationLoop { ProjectId = projectId, Status = IterationLoopStatus.Running });
		}

		public Task StopAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.CompletedTask;
		public Task PauseAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.CompletedTask;
		public Task ResumeAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.CompletedTask;
		public Task<IterationLoop?> GetStatusAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(loop);
		public Task<List<IterationLoop>> GetHistoryAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(new List<IterationLoop>());
		public Task<IterationLoop> UpdateConfigAsync(Guid projectId, AutoPilotConfig config, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}
}
