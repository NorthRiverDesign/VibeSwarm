using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using VibeSwarm.Client.Components.Common;
using VibeSwarm.Client.Services;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

public sealed class QueueDropdownPanelTests
{
	[Fact]
	public void QueueDropdownPanel_RendersRunningJobsAndUpcomingIdeas()
	{
		using var context = CreateContext(new FakeIdeaService(
			new GlobalQueueSnapshot
			{
				RunningJobsCount = 1,
				UpcomingIdeasCount = 2,
				ProjectsCurrentlyProcessing = 1,
				RunningJobs =
				[
					new GlobalQueueJobSummary
					{
						Id = Guid.NewGuid(),
						ProjectId = Guid.NewGuid(),
						ProjectName = "Alpha",
						Title = "Fix auth refresh",
						GoalPrompt = "Fix auth refresh",
						Status = JobStatus.Processing,
						ProviderName = "Claude",
						CurrentActivity = "Running tests"
					}
				],
				UpcomingIdeas =
				[
					new GlobalQueueIdeaSummary
					{
						IdeaId = Guid.NewGuid(),
						ProjectId = Guid.NewGuid(),
						ProjectName = "Beta",
						Description = "Add queue controls to navbar",
						IsProjectProcessing = true
					},
					new GlobalQueueIdeaSummary
					{
						IdeaId = Guid.NewGuid(),
						ProjectId = Guid.NewGuid(),
						ProjectName = "Gamma",
						Description = "Tighten mobile spacing",
						IsProjectProcessing = false
					}
				]
			}));

		var cut = context.Render<QueueDropdownPanel>();

		Assert.Contains("Queue", cut.Markup);
		Assert.Contains("1 running", cut.Markup);
		Assert.Contains("2 upcoming", cut.Markup);
		Assert.Contains("Running Jobs", cut.Markup);
		Assert.Contains("Upcoming Ideas", cut.Markup);
		Assert.Contains("Fix auth refresh", cut.Markup);
		Assert.Contains("Running tests", cut.Markup);
		Assert.Contains("Add queue controls to navbar", cut.Markup);
		Assert.Contains("Queued", cut.Markup);
		Assert.Contains("Pending", cut.Markup);
		Assert.Contains("All Jobs", cut.Markup);
	}

	[Fact]
	public void QueueDropdownPanel_ShowsQueuedBadge_ForStartedIdeasWithQueuedJobs()
	{
		using var context = CreateContext(new FakeIdeaService(
			new GlobalQueueSnapshot
			{
				UpcomingIdeasCount = 1,
				UpcomingIdeas =
				[
					new GlobalQueueIdeaSummary
					{
						IdeaId = Guid.NewGuid(),
						ProjectId = Guid.NewGuid(),
						ProjectName = "Alpha",
						Description = "Queued started idea",
						HasQueuedJob = true,
						IsProjectProcessing = false
					}
				]
			}));

		var cut = context.Render<QueueDropdownPanel>();

		Assert.Contains("Queued started idea", cut.Markup);
		Assert.Contains("Queued", cut.Markup);
		Assert.DoesNotContain("Pending", cut.Markup);
	}

	[Fact]
	public void QueueDropdownPanel_StartQueue_StartsGlobalProcessing()
	{
		var ideaService = new FakeIdeaService(
			new GlobalQueueSnapshot
			{
				RunningJobsCount = 0,
				UpcomingIdeasCount = 2,
				ProjectsCurrentlyProcessing = 0,
				UpcomingIdeas =
				[
					new GlobalQueueIdeaSummary
					{
						IdeaId = Guid.NewGuid(),
						ProjectId = Guid.NewGuid(),
						ProjectName = "Alpha",
						Description = "Queue me"
					}
				]
			},
			new GlobalQueueSnapshot
			{
				RunningJobsCount = 1,
				UpcomingIdeasCount = 1,
				ProjectsCurrentlyProcessing = 1
			});

		using var context = CreateContext(ideaService);
		var cut = context.Render<QueueDropdownPanel>();

		cut.FindAll("button").Single(button => button.TextContent.Contains("Start Queue")).Click();

		Assert.Equal(1, ideaService.StartAllProcessingCalls);
		Assert.Contains("Stop Queue", cut.Markup);
	}

	[Fact]
	public void QueueDropdownPanel_StopQueue_StopsGlobalProcessing()
	{
		var ideaService = new FakeIdeaService(
			new GlobalQueueSnapshot
			{
				RunningJobsCount = 1,
				UpcomingIdeasCount = 1,
				ProjectsCurrentlyProcessing = 1,
				RunningJobs =
				[
					new GlobalQueueJobSummary
					{
						Id = Guid.NewGuid(),
						ProjectId = Guid.NewGuid(),
						ProjectName = "Alpha",
						GoalPrompt = "Keep going",
						Status = JobStatus.Processing
					}
				]
			},
			new GlobalQueueSnapshot
			{
				RunningJobsCount = 1,
				UpcomingIdeasCount = 1,
				ProjectsCurrentlyProcessing = 0
			});

		using var context = CreateContext(ideaService);
		var cut = context.Render<QueueDropdownPanel>();

		cut.FindAll("button").Single(button => button.TextContent.Contains("Stop Queue")).Click();

		Assert.Equal(1, ideaService.StopAllProcessingCalls);
		Assert.Contains("Start Queue", cut.Markup);
	}

	[Fact]
	public void QueueDropdownPanel_CompactMode_UsesStaticDropdownDisplay()
	{
		using var context = CreateContext(new FakeIdeaService());
		var cut = context.Render<QueueDropdownPanel>(parameters => parameters
			.Add(component => component.Compact, true));

		var toggle = cut.Find("button[title='Queue']");
		var dropdown = cut.Find("div.dropdown");
		var menu = cut.Find("div.dropdown-menu");

		Assert.Equal("static", toggle.GetAttribute("data-bs-display"));
		Assert.Contains("mobile-header-dropdown", dropdown.ClassList);
		Assert.Contains("vs-nav-dropdown-menu", menu.ClassList);
	}

	[Fact]
	public async Task QueueDropdownPanel_RefreshRequest_UpdatesBadgeWithoutToggle()
	{
		var queuePanelStateService = new QueuePanelStateService();
		var ideaService = new FakeIdeaService(
			new GlobalQueueSnapshot
			{
				RunningJobsCount = 1,
				UpcomingIdeasCount = 1,
				ProjectsCurrentlyProcessing = 1
			},
			new GlobalQueueSnapshot
			{
				RunningJobsCount = 0,
				UpcomingIdeasCount = 1,
				ProjectsCurrentlyProcessing = 0
			});

		using var context = CreateContext(ideaService, queuePanelStateService);
		var cut = context.Render<QueueDropdownPanel>();

		Assert.Equal("2 queue items", cut.Find(".notification-bell-badge").GetAttribute("aria-label"));

		await cut.InvokeAsync(queuePanelStateService.RequestRefreshAsync);

		cut.WaitForAssertion(() =>
		{
			Assert.Equal("1 queue item", cut.Find(".notification-bell-badge").GetAttribute("aria-label"));
			Assert.Contains("Start Queue", cut.Markup);
		});
	}

	private static BunitContext CreateContext(FakeIdeaService ideaService, QueuePanelStateService? queuePanelStateService = null)
	{
		var context = new BunitContext();
		context.Services.AddLogging();
		context.Services.AddSingleton<IIdeaService>(ideaService);
		context.Services.AddSingleton<NotificationService>();
		context.Services.AddSingleton(queuePanelStateService ?? new QueuePanelStateService());
		context.Services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());
		return context;
	}

	private sealed class FakeIdeaService(params GlobalQueueSnapshot[] snapshots) : IIdeaService
	{
		private readonly Queue<GlobalQueueSnapshot> _snapshots = new(snapshots.Length == 0 ? [new GlobalQueueSnapshot()] : snapshots);

		public int StartAllProcessingCalls { get; private set; }
		public int StopAllProcessingCalls { get; private set; }

		public Task<IEnumerable<Idea>> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Idea>>([]);
		public Task<ProjectIdeasListResult> GetPagedByProjectIdAsync(Guid projectId, int page = 1, int pageSize = 10, CancellationToken cancellationToken = default) => Task.FromResult(new ProjectIdeasListResult());
		public Task<Idea?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Idea?>(null);
		public Task<Idea> CreateAsync(Idea idea, CancellationToken cancellationToken = default) => Task.FromResult(idea);
		public Task<Idea> CreateAsync(CreateIdeaRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new Idea());
		public Task<Idea> UpdateAsync(Idea idea, CancellationToken cancellationToken = default) => Task.FromResult(idea);
		public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
		public Task<Idea?> GetNextUnprocessedAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<Idea?>(null);
		public Task<Job?> ConvertToJobAsync(Guid ideaId, IdeaProcessingOptions? options = null, CancellationToken cancellationToken = default) => Task.FromResult<Job?>(null);
		public Task<bool> CompleteIdeaFromJobAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.FromResult(false);
		public Task<bool> HandleJobCompletionAsync(Guid jobId, bool success, CancellationToken cancellationToken = default) => Task.FromResult(false);
		public Task<Idea?> GetByJobIdAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.FromResult<Idea?>(null);
		public Task<IdeaAttachment?> GetAttachmentAsync(Guid attachmentId, CancellationToken cancellationToken = default) => Task.FromResult<IdeaAttachment?>(null);
		public Task StartProcessingAsync(Guid projectId, IdeaProcessingOptions? options = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
		public Task StopProcessingAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.CompletedTask;
		public Task<bool> IsProcessingActiveAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(false);
		public Task<bool> ProcessNextIdeaIfReadyAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(false);
		public Task<IEnumerable<Guid>> GetActiveProcessingProjectsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Guid>>([]);
		public Task RecoverStuckIdeasAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
		public Task ReorderIdeasAsync(Guid projectId, IEnumerable<Guid> ideaIdsInOrder, CancellationToken cancellationToken = default) => Task.CompletedTask;
		public Task<Idea> CopyToProjectAsync(Guid ideaId, Guid targetProjectId, CancellationToken cancellationToken = default) => Task.FromResult(new Idea());
		public Task<Idea> MoveToProjectAsync(Guid ideaId, Guid targetProjectId, CancellationToken cancellationToken = default) => Task.FromResult(new Idea());
		public Task<Idea?> ExpandIdeaAsync(Guid ideaId, IdeaExpansionRequest? request = null, CancellationToken cancellationToken = default) => Task.FromResult<Idea?>(null);
		public Task<Idea?> CancelExpansionAsync(Guid ideaId, CancellationToken cancellationToken = default) => Task.FromResult<Idea?>(null);
		public Task<Idea?> ApproveExpansionAsync(Guid ideaId, string? editedDescription = null, CancellationToken cancellationToken = default) => Task.FromResult<Idea?>(null);
		public Task<Idea?> RejectExpansionAsync(Guid ideaId, CancellationToken cancellationToken = default) => Task.FromResult<Idea?>(null);
		public Task<GlobalIdeasProcessingStatus> GetGlobalProcessingStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new GlobalIdeasProcessingStatus());
		public Task<GlobalQueueSnapshot> GetGlobalQueueSnapshotAsync(CancellationToken cancellationToken = default)
		{
			if (_snapshots.Count > 1)
			{
				return Task.FromResult(_snapshots.Dequeue());
			}

			return Task.FromResult(_snapshots.Peek());
		}

		public Task StartAllProcessingAsync(IdeaProcessingOptions? options = null, CancellationToken cancellationToken = default)
		{
			StartAllProcessingCalls++;
			return Task.CompletedTask;
		}

		public Task StopAllProcessingAsync(CancellationToken cancellationToken = default)
		{
			StopAllProcessingCalls++;
			return Task.CompletedTask;
		}

		public Task<SuggestIdeasResult> SuggestIdeasFromCodebaseAsync(Guid projectId, SuggestIdeasRequest? request = null, CancellationToken cancellationToken = default)
			=> Task.FromResult(new SuggestIdeasResult());
	}

	private sealed class NoOpJsRuntime : IJSRuntime
	{
		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
			=> ValueTask.FromResult(default(TValue)!);

		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
			=> ValueTask.FromResult(default(TValue)!);
	}
}
