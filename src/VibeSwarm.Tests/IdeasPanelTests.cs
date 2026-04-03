using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using VibeSwarm.Client.Components.Ideas;
using VibeSwarm.Client.Services;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Inference;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.Validation;

namespace VibeSwarm.Tests;

public sealed class IdeasPanelTests
{
	[Fact]
	public async Task RenderedIdeasPanel_ShowsCompactStatusAndActionHints()
	{
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSingleton<IProjectService>(new FakeProjectService());
		services.AddSingleton<IIdeaService>(new FakeIdeaService());
		services.AddSingleton<IJobService>(new FakeJobService());
		services.AddSingleton<NotificationService>();
		services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		var idea = new Idea
		{
			Id = Guid.NewGuid(),
			Description = "Refresh the project details layout",
			ProjectId = Guid.NewGuid()
		};

		var html = await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
			{
				[nameof(IdeasPanel.Ideas)] = new List<Idea> { idea },
				[nameof(IdeasPanel.TotalIdeasCount)] = 7,
				[nameof(IdeasPanel.UnprocessedIdeasCount)] = 3,
				[nameof(IdeasPanel.IsPageLoading)] = true,
				[nameof(IdeasPanel.HasDefaultProvider)] = false,
				[nameof(IdeasPanel.HasInference)] = true,
				[nameof(IdeasPanel.AvailableInferenceProviders)] = new List<InferenceProvider>(),
				[nameof(IdeasPanel.AvailableProviders)] = new List<Provider>()
			});
			var output = await renderer.RenderComponentAsync<IdeasPanel>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("Refreshing ideas", html);
		Assert.Contains("3 pending", html);
		Assert.Contains(">Ideas</h3>", html);
		Assert.Contains("Start All", html);
		Assert.Contains("Set default provider", html);
		Assert.Contains("Add idea", html);
		Assert.Contains("Set a default provider to enable idea processing", html);
		Assert.Contains("Paste images directly into the idea box to attach them.", html);
		Assert.DoesNotContain("class=\"badge bg-secondary\">7</span>", html);
		Assert.DoesNotContain("Short description of a feature or update.", html);
		Assert.DoesNotContain("card-header", html);
		Assert.Contains($"maxlength=\"{ValidationLimits.IdeaDescriptionMaxLength}\"", html);
		Assert.Contains($"0/{ValidationLimits.IdeaDescriptionMaxLength} characters", html);
		Assert.Contains("border rounded-3", html);
		Assert.DoesNotContain("border rounded-3 overflow-hidden", html);
	}

	[Fact]
	public async Task RenderedIdeasPanel_ShowsSuggestButton_WhenConfiguredProvidersExistWithoutInference()
	{
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSingleton<IProjectService>(new FakeProjectService());
		services.AddSingleton<IIdeaService>(new FakeIdeaService());
		services.AddSingleton<IJobService>(new FakeJobService());
		services.AddSingleton<NotificationService>();
		services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		var html = await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
			{
				[nameof(IdeasPanel.HasInference)] = false,
				[nameof(IdeasPanel.AvailableInferenceProviders)] = new List<InferenceProvider>(),
				[nameof(IdeasPanel.AvailableProviders)] =
					new List<Provider> { new() { Id = Guid.NewGuid(), Name = "Copilot", Type = ProviderType.Copilot, IsEnabled = true } }
			});
			var output = await renderer.RenderComponentAsync<IdeasPanel>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("Suggest Ideas", html);
		Assert.DoesNotContain("Paste Image", html);
	}

	[Fact]
	public async Task IdeaListItem_UsesComfortableMobileSpacingAndJustifiedActions()
	{
		var services = new ServiceCollection();
		services.AddLogging();

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		var idea = new Idea
		{
			Id = Guid.NewGuid(),
			Description = "Tighten mobile spacing for idea actions",
			ProjectId = Guid.NewGuid()
		};

		var html = await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
			{
				[nameof(IdeaListItem.Idea)] = idea,
				[nameof(IdeaListItem.HasDefaultProvider)] = true
			});
			var output = await renderer.RenderComponentAsync<IdeaListItem>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("list-group-item p-3", html);
		Assert.Contains("justify-content-between align-items-stretch align-items-sm-start gap-3", html);
		Assert.Contains("align-self-stretch align-self-sm-start gap-2", html);
	}

	[Fact]
	public async Task IdeaListItem_RendersAttachedImagePreviewAndDownloadLink()
	{
		var services = new ServiceCollection();
		services.AddLogging();

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		var attachmentId = Guid.NewGuid();
		var idea = new Idea
		{
			Id = Guid.NewGuid(),
			Description = "Use the attached screenshot",
			ProjectId = Guid.NewGuid(),
			Attachments =
			[
				new IdeaAttachment
				{
					Id = attachmentId,
					FileName = "mockup.png",
					ContentType = "image/png",
					RelativePath = Path.Combine(".vibeswarm", "idea-attachments", "mockup.png"),
					SizeBytes = 2048
				}
			]
		};

		var html = await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
			{
				[nameof(IdeaListItem.Idea)] = idea,
				[nameof(IdeaListItem.HasDefaultProvider)] = true
			});
			var output = await renderer.RenderComponentAsync<IdeaListItem>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains($"/api/ideas/attachments/{attachmentId}", html);
		Assert.Contains("mockup.png", html);
		Assert.Contains("2 KB", html);
		Assert.Contains("object-fit: cover;", html);
	}

	[Fact]
	public void StartAllModal_DefaultsProviderAndModelFromProjectSelection()
	{
		using var context = new BunitContext();
		context.Services.AddLogging();
		context.Services.AddSingleton<IProjectService>(new FakeProjectService());
		context.Services.AddSingleton<IIdeaService>(new FakeIdeaService());
		context.Services.AddSingleton<IJobService>(new FakeJobService());
		context.Services.AddSingleton<NotificationService>();
		context.Services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());
		context.JSInterop.SetupVoid("eval", _ => true);

		var primaryProviderId = Guid.NewGuid();
		var secondaryProviderId = Guid.NewGuid();
		IdeaProcessingOptions? submittedOptions = null;

		var cut = context.Render<IdeasPanel>(parameters => parameters
			.Add(component => component.Ideas, new List<Idea> { new() { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), Description = "Queue provider override" } })
			.Add(component => component.TotalIdeasCount, 1)
			.Add(component => component.UnprocessedIdeasCount, 1)
			.Add(component => component.HasDefaultProvider, true)
			.Add(component => component.IsGitRepository, true)
			.Add(component => component.AvailableProviders, new List<Provider>
			{
				new()
				{
					Id = primaryProviderId,
					Name = "Claude",
					Type = ProviderType.Claude,
					IsEnabled = true,
					AvailableModels =
					[
						new ProviderModel { ProviderId = primaryProviderId, ModelId = "claude-sonnet-4.6", DisplayName = "Claude Sonnet 4.6", IsAvailable = true, IsDefault = true },
						new ProviderModel { ProviderId = primaryProviderId, ModelId = "claude-opus-4.6", DisplayName = "Claude Opus 4.6", IsAvailable = true }
					]
				},
				new()
				{
					Id = secondaryProviderId,
					Name = "Copilot",
					Type = ProviderType.Copilot,
					IsEnabled = true,
					AvailableModels =
					[
						new ProviderModel { ProviderId = secondaryProviderId, ModelId = "gpt-5.4", DisplayName = "GPT-5.4", IsAvailable = true, IsDefault = true }
					]
				}
			})
			.Add(component => component.DefaultProcessingProviderId, primaryProviderId)
			.Add(component => component.DefaultProcessingModelId, "claude-opus-4.6")
			.Add(component => component.OnStartProcessingWithOptions, EventCallback.Factory.Create<IdeaProcessingOptions>(this, options => submittedOptions = options)));

		cut.Find("button[title='Start processing all pending ideas']").Click();

		var providerSelect = cut.Find("#startAllProvider");
		var modelSelect = cut.Find("#startAllModel");
		Assert.Equal(primaryProviderId.ToString(), providerSelect.GetAttribute("value"));
		Assert.Equal("claude-opus-4.6", modelSelect.GetAttribute("value"));

		modelSelect.Change("claude-sonnet-4.6");
		cut.FindAll("button.btn.btn-success").Last().Click();

		Assert.NotNull(submittedOptions);
		Assert.Equal(primaryProviderId, submittedOptions!.ProviderId);
		Assert.Equal("claude-sonnet-4.6", submittedOptions.ModelId);
		Assert.Equal(AutoCommitMode.Off, submittedOptions.AutoCommitMode);
	}

	[Fact]
	public async Task HandleClipboardImagePasted_ShowsImagePreviewAndIncludesAttachmentSummary()
	{
		using var context = new BunitContext();
		context.Services.AddLogging();
		context.Services.AddSingleton<IProjectService>(new FakeProjectService());
		context.Services.AddSingleton<IIdeaService>(new FakeIdeaService());
		context.Services.AddSingleton<IJobService>(new FakeJobService());
		context.Services.AddSingleton<NotificationService>();
		SetupEmptyIdeaDraftStorage(context);
		context.JSInterop.SetupVoid("vibeSwarmIdeas.registerPasteTarget", _ => true);

		var cut = context.Render<IdeasPanel>(parameters => parameters
			.Add(component => component.CurrentProjectId, Guid.NewGuid())
			.Add(component => component.HasInference, true)
			.Add(component => component.AvailableInferenceProviders, new List<InferenceProvider>())
			.Add(component => component.AvailableProviders, new List<Provider>()));

		await cut.InvokeAsync(async () =>
		{
			await cut.Instance.HandleClipboardImagePasted(new IdeasPanel.ClipboardAttachmentDto
			{
				FileName = "clipboard.png",
				ContentType = "image/png",
				Base64Content = Convert.ToBase64String([137, 80, 78, 71])
			});
		});

		var html = cut.Markup;

		Assert.Contains("clipboard.png", html);
		Assert.Contains("1 attachment", html);
		Assert.Contains("data:image/png;base64,", html);
		Assert.Contains("clipboard.png\" class=\"rounded border", html);
	}

	[Fact]
	public void Render_RestoresSavedIdeaDraftFromSessionStorage()
	{
		using var context = new BunitContext();
		context.Services.AddLogging();
		context.Services.AddSingleton<IProjectService>(new FakeProjectService());
		context.Services.AddSingleton<IIdeaService>(new FakeIdeaService());
		context.Services.AddSingleton<IJobService>(new FakeJobService());
		context.Services.AddSingleton<NotificationService>();

		var projectId = Guid.NewGuid();
		var storageKey = $"vibeswarm.idea-draft.{projectId:N}";
		const string savedDraft = "Restore this idea after a reload";

		context.JSInterop.Setup<string>("sessionStorage.getItem", invocation =>
			invocation.Arguments.Count == 1 && string.Equals(invocation.Arguments[0]?.ToString(), storageKey, StringComparison.Ordinal))
			.SetResult(savedDraft);
		context.JSInterop.SetupVoid("vibeSwarmIdeas.registerPasteTarget", _ => true);

		var cut = context.Render<IdeasPanel>(parameters => parameters
			.Add(component => component.CurrentProjectId, projectId)
			.Add(component => component.HasInference, true)
			.Add(component => component.AvailableInferenceProviders, new List<InferenceProvider>())
			.Add(component => component.AvailableProviders, new List<Provider>()));

		cut.WaitForAssertion(() =>
		{
			Assert.Equal(savedDraft, cut.Find("textarea").GetAttribute("value"));
		});
	}

	[Fact]
	public void AddIdea_PersistsDraftWhileTyping_AndClearsItAfterSubmit()
	{
		using var context = new BunitContext();
		context.Services.AddLogging();
		context.Services.AddSingleton<IProjectService>(new FakeProjectService());
		context.Services.AddSingleton<IIdeaService>(new FakeIdeaService());
		context.Services.AddSingleton<IJobService>(new FakeJobService());
		context.Services.AddSingleton<NotificationService>();

		var projectId = Guid.NewGuid();
		var storageKey = $"vibeswarm.idea-draft.{projectId:N}";
		CreateIdeaRequest? submittedRequest = null;

		context.JSInterop.Setup<string?>("sessionStorage.getItem", invocation =>
			invocation.Arguments.Count == 1 && string.Equals(invocation.Arguments[0]?.ToString(), storageKey, StringComparison.Ordinal))
			.SetResult(null);
		context.JSInterop.SetupVoid("sessionStorage.setItem", invocation =>
			invocation.Arguments.Count == 2 &&
			string.Equals(invocation.Arguments[0]?.ToString(), storageKey, StringComparison.Ordinal) &&
			string.Equals(invocation.Arguments[1]?.ToString(), "Keep this draft", StringComparison.Ordinal));
		context.JSInterop.SetupVoid("sessionStorage.removeItem", invocation =>
			invocation.Arguments.Count == 1 && string.Equals(invocation.Arguments[0]?.ToString(), storageKey, StringComparison.Ordinal));
		context.JSInterop.SetupVoid("vibeSwarmIdeas.registerPasteTarget", _ => true);

		var cut = context.Render<IdeasPanel>(parameters => parameters
			.Add(component => component.CurrentProjectId, projectId)
			.Add(component => component.OnAddIdea, EventCallback.Factory.Create<CreateIdeaRequest>(this, request => submittedRequest = request))
			.Add(component => component.HasInference, true)
			.Add(component => component.AvailableInferenceProviders, new List<InferenceProvider>())
			.Add(component => component.AvailableProviders, new List<Provider>()));

		cut.Find("textarea").Input("Keep this draft");
		cut.Find("button.btn.btn-primary").Click();

		Assert.NotNull(submittedRequest);
		Assert.Equal("Keep this draft", submittedRequest!.Description);
		Assert.Equal(string.Empty, cut.Find("textarea").GetAttribute("value"));
	}

	[Fact]
	public void Render_DoesNotCrash_WhenPasteInteropScriptIsUnavailable()
	{
		using var context = new BunitContext();
		context.Services.AddLogging();
		context.Services.AddSingleton<IProjectService>(new FakeProjectService());
		context.Services.AddSingleton<IIdeaService>(new FakeIdeaService());
		context.Services.AddSingleton<IJobService>(new FakeJobService());
		var notificationService = new NotificationService();
		context.Services.AddSingleton(notificationService);
		SetupEmptyIdeaDraftStorage(context);

		context.JSInterop
			.SetupVoid("vibeSwarmIdeas.registerPasteTarget", _ => true)
			.SetException(new JSException("Could not find 'vibeSwarmIdeas.registerPasteTarget' ('vibeSwarmIdeas' was undefined)."));

		var cut = context.Render<IdeasPanel>(parameters => parameters
			.Add(component => component.CurrentProjectId, Guid.NewGuid())
			.Add(component => component.HasInference, true)
			.Add(component => component.AvailableInferenceProviders, new List<InferenceProvider>())
			.Add(component => component.AvailableProviders, new List<Provider>()));

		cut.WaitForAssertion(() =>
		{
			Assert.Contains("Attach Files", cut.Markup);
			Assert.Contains("disabled opacity-50 pe-none", cut.Markup);
			Assert.DoesNotContain("Paste images directly into the idea box to attach them.", cut.Markup);
		});

		var warning = Assert.Single(notificationService.Notifications);
		Assert.Equal(NotificationType.Warning, warning.Type);
		Assert.Equal("Attachments unavailable", warning.Title);
		Assert.Contains("temporarily unavailable", warning.Message);
	}

	[Fact]
	public void IdeasListHeader_ShowsPendingCountAndStartAllButton()
	{
		using var context = new BunitContext();
		context.Services.AddLogging();
		context.Services.AddSingleton<IProjectService>(new FakeProjectService());
		context.Services.AddSingleton<IIdeaService>(new FakeIdeaService());
		context.Services.AddSingleton<IJobService>(new FakeJobService());
		context.Services.AddSingleton<NotificationService>();
		SetupEmptyIdeaDraftStorage(context);
		context.JSInterop.SetupVoid("vibeSwarmIdeas.registerPasteTarget", _ => true);

		var cut = context.Render<IdeasPanel>(parameters => parameters
			.Add(component => component.Ideas, new List<Idea> { new() { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), Description = "Queued idea" } })
			.Add(component => component.TotalIdeasCount, 1)
			.Add(component => component.UnprocessedIdeasCount, 1)
			.Add(component => component.HasDefaultProvider, true)
			.Add(component => component.HasInference, true)
			.Add(component => component.AvailableInferenceProviders, new List<InferenceProvider>())
			.Add(component => component.AvailableProviders, new List<Provider>()));

		Assert.Contains("Ideas", cut.Markup);
		Assert.Contains("1 pending", cut.Markup);
		Assert.NotNull(cut.Find("button[title='Start processing all pending ideas']"));
	}

	private sealed class FakeProjectService : IProjectService
	{
		public Task<IEnumerable<Project>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Project>>([]);
		public Task<IEnumerable<Project>> GetRecentAsync(int count, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Project>>([]);
		public Task<Project?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Project?>(null);
		public Task<Project?> GetByIdWithJobsAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Project?>(null);
		public Task<Project> CreateAsync(Project project, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Project> CreateProjectAsync(ProjectCreationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Project> UpdateAsync(Project project, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IEnumerable<ProjectWithStats>> GetAllWithStatsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<ProjectWithStats>>([]);
		public Task<IEnumerable<DashboardProjectInfo>> GetRecentWithLatestJobAsync(int count, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<DashboardProjectInfo>>([]);
		public Task<DashboardJobMetrics> GetDashboardJobMetricsAsync(int rangeDays, CancellationToken cancellationToken = default)
			=> Task.FromResult(new DashboardJobMetrics
			{
				RangeDays = rangeDays,
				Buckets = []
			});
		public Task<IEnumerable<DashboardRunningJobInfo>> GetDashboardRunningJobsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<DashboardRunningJobInfo>>([]);
	}

	private sealed class FakeIdeaService : IIdeaService
	{
		public Task<IEnumerable<Idea>> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Idea>>([]);
		public Task<ProjectIdeasListResult> GetPagedByProjectIdAsync(Guid projectId, int page = 1, int pageSize = 10, CancellationToken cancellationToken = default)
			=> Task.FromResult(new ProjectIdeasListResult());
		public Task<Idea?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Idea?>(null);
		public Task<Idea> CreateAsync(Idea idea, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Idea> CreateAsync(CreateIdeaRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Idea> UpdateAsync(Idea idea, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Idea?> GetNextUnprocessedAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<Idea?>(null);
		public Task<Job?> ConvertToJobAsync(Guid ideaId, IdeaProcessingOptions? options = null, CancellationToken cancellationToken = default) => Task.FromResult<Job?>(null);
		public Task<bool> CompleteIdeaFromJobAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.FromResult(false);
		public Task<bool> HandleJobCompletionAsync(Guid jobId, bool success, CancellationToken cancellationToken = default) => Task.FromResult(false);
		public Task<Idea?> GetByJobIdAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.FromResult<Idea?>(null);
		public Task<IdeaAttachment?> GetAttachmentAsync(Guid attachmentId, CancellationToken cancellationToken = default) => Task.FromResult<IdeaAttachment?>(null);
		public Task StartProcessingAsync(Guid projectId, IdeaProcessingOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task StopProcessingAsync(Guid projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> IsProcessingActiveAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(false);
		public Task<bool> ProcessNextIdeaIfReadyAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(false);
		public Task<IEnumerable<Guid>> GetActiveProcessingProjectsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Guid>>([]);
		public Task RecoverStuckIdeasAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
		public Task ReorderIdeasAsync(Guid projectId, IEnumerable<Guid> ideaIdsInOrder, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Idea> CopyToProjectAsync(Guid ideaId, Guid targetProjectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Idea> MoveToProjectAsync(Guid ideaId, Guid targetProjectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Idea?> ExpandIdeaAsync(Guid ideaId, IdeaExpansionRequest? request = null, CancellationToken cancellationToken = default) => Task.FromResult<Idea?>(null);
		public Task<Idea?> CancelExpansionAsync(Guid ideaId, CancellationToken cancellationToken = default) => Task.FromResult<Idea?>(null);
		public Task<Idea?> ApproveExpansionAsync(Guid ideaId, string? editedDescription = null, CancellationToken cancellationToken = default) => Task.FromResult<Idea?>(null);
		public Task<Idea?> RejectExpansionAsync(Guid ideaId, CancellationToken cancellationToken = default) => Task.FromResult<Idea?>(null);
		public Task<SuggestIdeasResult> SuggestIdeasFromCodebaseAsync(Guid projectId, SuggestIdeasRequest? request = null, CancellationToken cancellationToken = default)
			=> Task.FromResult(new SuggestIdeasResult());
		public Task<GlobalIdeasProcessingStatus> GetGlobalProcessingStatusAsync(CancellationToken cancellationToken = default)
			=> Task.FromResult(new GlobalIdeasProcessingStatus());
		public Task StartAllProcessingAsync(IdeaProcessingOptions? options = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
		public Task StopAllProcessingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
	}

	private sealed class FakeJobService : IJobService
	{
		public Task<IEnumerable<Job>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Job>>([]);
		public Task<JobsListResult> GetPagedAsync(Guid? projectId = null, string statusFilter = "all", int page = 1, int pageSize = 25, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IEnumerable<Job>> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Job>>([]);
		public Task<ProjectJobsListResult> GetPagedByProjectIdAsync(Guid projectId, int page = 1, int pageSize = 10, string? search = null, string statusFilter = "all", CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IEnumerable<Job>> GetPendingJobsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Job>>([]);
		public Task<IEnumerable<JobSummary>> GetActiveJobsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<JobSummary>>([]);
		public Task<Job?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Job?>(null);
		public Task<Job?> GetByIdWithMessagesAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Job> CreateAsync(Job job, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Job> UpdateStatusAsync(Guid id, JobStatus status, string? output = null, string? errorMessage = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Job> UpdateJobResultAsync(Guid id, JobStatus status, string? sessionId, string? output, string? errorMessage, int? inputTokens, int? outputTokens, decimal? costUsd, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task AddMessageAsync(Guid jobId, JobMessage message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task AddMessagesAsync(Guid jobId, IEnumerable<JobMessage> messages, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> RequestCancellationAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(false);
		public Task<bool> ForceCancelAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> IsCancellationRequestedAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task UpdateProgressAsync(Guid id, string? currentActivity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> ResetJobAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> UpdateGitCommitHashAsync(Guid id, string commitHash, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> UpdateGitDiffAsync(Guid id, string? gitDiff, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> UpdateGitDeliveryAsync(Guid id, string? commitHash = null, int? pullRequestNumber = null, string? pullRequestUrl = null, DateTime? pullRequestCreatedAt = null, DateTime? mergedAt = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> PauseForInteractionAsync(Guid id, string interactionPrompt, string interactionType, string? choices = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<(string? Prompt, string? Type, string? Choices)?> GetPendingInteractionAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> ResumeJobAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> ContinueJobAsync(Guid id, string followUpPrompt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IEnumerable<Job>> GetPausedJobsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<string?> GetLastUsedModelAsync(Guid projectId, Guid providerId, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
		public Task<bool> ResetJobWithOptionsAsync(Guid id, Guid? providerId = null, string? modelId = null, string? reasoningEffort = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> UpdateJobPromptAsync(Guid id, string newPrompt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<int> CancelAllByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<int> DeleteCompletedByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<int> RetrySelectedByProjectIdAsync(Guid projectId, IReadOnlyCollection<Guid> jobIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<int> CancelSelectedByProjectIdAsync(Guid projectId, IReadOnlyCollection<Guid> jobIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<int> PrioritizeSelectedByProjectIdAsync(Guid projectId, IReadOnlyCollection<Guid> jobIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> ForceFailJobAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task RefreshExecutionPlanAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
	}

	private sealed class NoOpJsRuntime : IJSRuntime
	{
		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
			=> ValueTask.FromResult(default(TValue)!);

		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
			=> ValueTask.FromResult(default(TValue)!);
	}

	private static void SetupEmptyIdeaDraftStorage(BunitContext context)
	{
		context.JSInterop.Setup<string>("sessionStorage.getItem", _ => true).SetResult(string.Empty);
	}
}
