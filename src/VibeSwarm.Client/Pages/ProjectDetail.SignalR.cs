using Microsoft.AspNetCore.SignalR.Client;
using VibeSwarm.Client.Services;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Client.Pages;

public partial class ProjectDetail
{
	private HubConnection? _hubConnection;
	private CancellationTokenSource? _signalRCts;
	private HashSet<Guid> _localIdeaUpdateIds = new();
	private HashSet<Guid> _localIdeaCreateIds = new();
	private DateTime _lastIdeasLoadTime = DateTime.MinValue;
	private bool _disposed;
	private AppResumeService? _appResumeService;

	private static readonly TimeSpan PollingIntervalActive = TimeSpan.FromSeconds(10);
	private static readonly TimeSpan PollingIntervalSignalRFallback = TimeSpan.FromSeconds(30);
	private bool IsSignalRConnected => _hubConnection?.State == HubConnectionState.Connected;

	protected override async Task OnInitializedAsync()
	{
		// Back from the background: the job pushes sent meanwhile were lost, so reload.
		_appResumeService = ServiceProvider.GetService<AppResumeService>();
		if (_appResumeService is not null)
		{
			_appResumeService.Resumed += OnAppResumedAsync;
		}

		// Back on the page: draw what it last showed straight away; LoadData reloads it in place.
		RestoreSnapshot();
		await LoadData();

		_ = InitializeSignalRSafe();
		StartAutoRefresh();
	}

	// Reloads in place: LoadData would show the loading state and reset both lists to page one.
	private Task OnAppResumedAsync() => InvokeAsync(async () =>
	{
		if (_disposed || Project == null) return;

		await RefreshJobs(force: true);
		await LoadIdeas(force: true);
		if (IsAutoPilotRunning)
			await RefreshAutoPilotAsync();
		StateHasChanged();
	});

	private async Task LoadGitInfoSafe()
	{
		try
		{
			await LoadGitInfo();
		}
		catch (Exception)
		{
			_isLoadingGitInfo = false;
			IsGitRepository = false;
		}
		finally
		{
			await InvokeAsync(StateHasChanged);
		}

		await LoadRemoteSyncStatusAsync();
	}

	private async Task InitializeSignalRSafe()
	{
		try
		{
			_signalRCts = new CancellationTokenSource();

			using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
			using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
			_signalRCts.Token, timeoutCts.Token);

			await InitializeSignalR(linkedCts.Token);
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception)
		{
		}
	}

	private async Task InitializeSignalR(CancellationToken cancellationToken)
	{
		_hubConnection = new HubConnectionBuilder()
		.WithUrl(NavigationManager.ToAbsoluteUri("/hubs/job"))
		.WithAutomaticReconnect(new KeepReconnectingPolicy())
		.Build();

		RegisterSignalRHandlers();

		_hubConnection.Closed += async (error) =>
		{
			await Task.CompletedTask;
		};

		_hubConnection.Reconnected += async (connectionId) =>
		{
			await SubscribeToSignalRGroups();
		};

		try
		{
			await _hubConnection.StartAsync(cancellationToken);
			await SubscribeToSignalRGroups();
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
		}
	}

	private void RegisterSignalRHandlers()
	{
		if (_hubConnection == null) return;

		_hubConnection.On<string, string>("JobStatusChanged", async (jobId, status) =>
		{
			if (_disposed) return;
			try
			{
				await InvokeAsync(async () =>
				{
					if (_disposed) return;
					await RefreshJobs();
					await LoadIdeas();
					if (IsAutoPilotRunning)
						await RefreshAutoPilotAsync();
					StateHasChanged();
				});
			}
			catch (ObjectDisposedException) { }
			catch (Exception) { }
		});

		_hubConnection.On<string, string>("JobCreated", async (jobId, projectId) =>
		{
			if (_disposed) return;
			try
			{
				await InvokeAsync(async () =>
				{
					if (_disposed) return;
					await RefreshJobs();
					await LoadIdeas();
					if (IsAutoPilotRunning && projectId == ProjectId.ToString())
						await RefreshAutoPilotAsync();
					StateHasChanged();
				});
			}
			catch (ObjectDisposedException) { }
			catch (Exception) { }
		});

		_hubConnection.On<string, string>("JobDeleted", async (jobId, projectId) =>
		{
			if (_disposed) return;
			try
			{
				await InvokeAsync(async () =>
				{
					if (_disposed) return;
					await RefreshJobs();
					StateHasChanged();
				});
			}
			catch (ObjectDisposedException) { }
			catch (Exception) { }
		});

		_hubConnection.On<string, string, DateTime>("JobActivityUpdated", async (jobId, activity, timestamp) =>
		{
			if (_disposed) return;
			try
			{
				await InvokeAsync(() =>
				{
					if (_disposed) return Task.CompletedTask;
					var pagedJob = PagedJobs.FirstOrDefault(j => j.Id.ToString() == jobId);
					if (pagedJob != null)
					{
						pagedJob.CurrentActivity = activity;
						StateHasChanged();
					}
					if (_activeJobSummary?.Id.ToString() == jobId)
					{
						_activeJobSummary.CurrentActivity = activity;
						StateHasChanged();
					}
					return Task.CompletedTask;
				});
			}
			catch (ObjectDisposedException) { }
			catch (Exception) { }
		});

		_hubConnection.On<string, bool, string?>("JobCompleted", async (jobId, success, errorMessage) =>
		{
			if (_disposed) return;
			try
			{
				await InvokeAsync(async () =>
				{
					if (_disposed) return;
					await RefreshJobs();
					await LoadIdeas();
					await RefreshUncommittedChangesStatus();
					_ = LoadRemoteSyncStatusAsync();
					ProcessingIdeaIds.Clear();
					if (IsAutoPilotRunning)
						await RefreshAutoPilotAsync();
					if (_releaseTab != null)
						await _releaseTab.RefreshAsync();

					if (success && (_activeTab == "changes" || _hasUncommittedChangesHeader))
					{
						_changesJobs = null; // Invalidate so next load gets fresh data
						if (_activeTab == "changes")
						{
							await EnsureChangesJobsLoadedAsync(force: true);
						}
						await LoadChangesTabData();
					}

					StateHasChanged();
				});
			}
			catch (ObjectDisposedException) { }
			catch (Exception) { }
		});

		_hubConnection.On<string, bool>("JobGitDiffUpdated", async (jobId, hasChanges) =>
		{
			if (_disposed) return;
			try
			{
				await InvokeAsync(async () =>
				{
					if (_disposed) return;
					await RefreshJobs();
					await RefreshUncommittedChangesStatus();

					if (hasChanges || _activeTab == "changes")
					{
						_changesJobs = null; // Invalidate so next load gets fresh data
						if (_activeTab == "changes")
						{
							await EnsureChangesJobsLoadedAsync(force: true);
						}
						await LoadChangesTabData();
					}

					StateHasChanged();
				});
			}
			catch (ObjectDisposedException) { }
			catch (Exception) { }
		});

		_hubConnection.On<string, string, string>("IdeaStarted", async (ideaId, projectId, jobId) =>
		{
			if (_disposed) return;
			try
			{
				await InvokeAsync(async () =>
				{
					if (_disposed) return;
					if (projectId == ProjectId.ToString())
					{
						await RefreshJobs();
						await LoadIdeas();
						StateHasChanged();
					}
				});
			}
			catch (ObjectDisposedException) { }
			catch (Exception) { }
		});

		_hubConnection.On<string, bool>("IdeasProcessingStateChanged", async (projectId, isActive) =>
		{
			if (_disposed) return;
			try
			{
				await InvokeAsync(async () =>
				{
					if (_disposed) return;
					if (projectId == ProjectId.ToString())
					{
						IsIdeasProcessingActive = isActive;
						if (!isActive)
						{
							ProcessingIdeaIds.Clear();
						}
						await LoadIdeas();
						StateHasChanged();
					}
				});
			}
			catch (ObjectDisposedException) { }
			catch (Exception) { }
		});

		_hubConnection.On<string, string>("IdeaCreated", async (ideaId, projectId) =>
		{
			if (_disposed) return;
			try
			{
				await InvokeAsync(async () =>
				{
					if (_disposed) return;
					if (projectId == ProjectId.ToString())
					{
						if (Guid.TryParse(ideaId, out var createdIdeaId) && _localIdeaCreateIds.Remove(createdIdeaId))
						{
							return;
						}
						await LoadIdeas();
						StateHasChanged();
					}
				});
			}
			catch (ObjectDisposedException) { }
			catch (Exception) { }
		});

		_hubConnection.On<string, string>("IdeaDeleted", async (ideaId, projectId) =>
		{
			if (_disposed) return;
			try
			{
				await InvokeAsync(async () =>
				{
					if (_disposed) return;
					if (projectId == ProjectId.ToString())
					{
						if (Guid.TryParse(ideaId, out var ideaGuid))
						{
							ProcessingIdeaIds.Remove(ideaGuid);
						}
						await LoadIdeas();
						StateHasChanged();
					}
				});
			}
			catch (ObjectDisposedException) { }
			catch (Exception) { }
		});

		_hubConnection.On<string, string>("IdeaUpdated", async (ideaId, projectId) =>
		{
			if (_disposed) return;
			try
			{
				await InvokeAsync(async () =>
				{
					if (_disposed) return;
					if (projectId == ProjectId.ToString())
					{
						if (Guid.TryParse(ideaId, out var updatedIdeaId) && _localIdeaUpdateIds.Remove(updatedIdeaId))
						{
							StateHasChanged();
							return;
						}
						await LoadIdeas();
						StateHasChanged();
					}
				});
			}
			catch (ObjectDisposedException) { }
			catch (Exception) { }
		});

		_hubConnection.On<string, string, int, int>(
			"AutoPilotStateChanged", async (projectId, status, completedIterations, maxIterations) =>
		{
			if (_disposed) return;
			try
			{
				await InvokeAsync(async () =>
				{
					if (_disposed) return;
					if (projectId == ProjectId.ToString())
					{
						await RefreshAutoPilotAsync();
						StateHasChanged();
					}
				});
			}
			catch (ObjectDisposedException) { }
			catch (Exception) { }
		});
	}

	/// <summary>
	/// Reloads the auto-pilot status for the header card and the Auto-Pilot tab. The loop announces
	/// its job when it queues it, so the job's own events refresh it too: otherwise the tab says
	/// Queued for as long as the job runs.
	/// </summary>
	private async Task RefreshAutoPilotAsync()
	{
		try
		{
			_autoPilotStatus = await AutoPilotService.GetStatusAsync(ProjectId);
		}
		catch
		{
			return;
		}

		_autoPilotPanel?.ShowStatus(_autoPilotStatus);
	}

	private async Task SubscribeToSignalRGroups()
	{
		if (_hubConnection?.State != HubConnectionState.Connected) return;

		try
		{
			await _hubConnection.InvokeAsync("SubscribeToProject", ProjectId.ToString());
			await _hubConnection.InvokeAsync("SubscribeToJobList");
		}
		catch (Exception)
		{
		}
	}

	private void StartAutoRefresh()
	{
		_refreshTimer = new System.Threading.Timer(async _ =>
		{
			if (_disposed) return;
			try
			{
				if (HasActiveJobs || IsIdeasProcessingActive || IsAutoPilotRunning)
				{
					if (IsSignalRConnected)
					{
						var timeSinceLastRefresh = DateTime.UtcNow - _lastJobsRefreshTime;
						if (timeSinceLastRefresh < PollingIntervalSignalRFallback)
							return;
					}

					await InvokeAsync(async () =>
					{
						if (_disposed) return;
						await RefreshJobs();
						await LoadIdeas();
						if (IsAutoPilotRunning)
							await RefreshAutoPilotAsync();
						StateHasChanged();
					});
				}
				else if (_activeTab == "changes" && IsGitRepository)
				{
					await InvokeAsync(async () =>
					{
						if (_disposed) return;
						await EnsureChangesJobsLoadedAsync();
						await RefreshUncommittedChangesStatus();
						await LoadChangesTabData();
						StateHasChanged();
					});
				}
			}
			catch (ObjectDisposedException) { }
			catch (Exception) { }
		}, null, PollingIntervalActive, PollingIntervalActive);
	}

	private bool _isLoadingJobs;
	private bool _isLoadingIdeas;

	private async Task LoadData()
	{
		// A page already showing the project (from the last visit, or before an edit) reloads in
		// place, without the spinner or the list placeholders.
		var isShowingProject = Project != null;
		IsLoading = !isShowingProject;
		try
		{
			Project = await ProjectService.GetByIdAsync(ProjectId);
			if (Project == null)
			{
				ForgetSnapshot();
				return;
			}

			// Git runs alongside the rest so the header doesn't wait for jobs, ideas and providers.
			_ = LoadGitInfoSafe();

			IsLoading = false;
			_isLoadingJobs = !isShowingProject;
			_isLoadingIdeas = !isShowingProject;
			StateHasChanged();

			_jobsPageNumber = 1;
			_ideasPageNumber = 1;

			var jobsTask = RefreshJobs(force: true).ContinueWith(_ =>
			{
				_isLoadingJobs = false;
			}, TaskScheduler.Default);

			var ideasTask = LoadIdeas(force: true).ContinueWith(_ =>
			{
				_isLoadingIdeas = false;
			}, TaskScheduler.Default);

			var providersTask = LoadProvidersAndModels();

			var autoPilotTask = Task.Run(async () =>
			{
				try { _autoPilotStatus = await AutoPilotService.GetStatusAsync(ProjectId); } catch { }
			});

			await Task.WhenAll(jobsTask, ideasTask, providersTask, autoPilotTask);
			SaveSnapshot();

			await InvokeAsync(StateHasChanged);
		}
		catch
		{
		}
		finally
		{
			IsLoading = false;
			_isLoadingJobs = false;
			_isLoadingIdeas = false;
		}
	}

	private async Task LoadProvidersAndModels()
	{
		try
		{
			Providers = (await ProviderService.GetAllAsync()).ToList();

			var suggestionTask = LoadSuggestionProviderModels();
			var inferenceTask = LoadInferenceModels();

			await Task.WhenAll(suggestionTask, inferenceTask);
		}
		catch
		{
			Providers = new List<Provider>();
		}
	}

	public async ValueTask DisposeAsync()
	{
		SaveSnapshot();
		_disposed = true;
		if (_appResumeService is not null)
		{
			_appResumeService.Resumed -= OnAppResumedAsync;
		}

		_expandCts?.Cancel();
		_expandCts?.Dispose();

		_signalRCts?.Cancel();
		_signalRCts?.Dispose();

		_refreshTimer?.Dispose();

		if (_hubConnection != null)
		{
			try
			{
				if (_hubConnection.State == HubConnectionState.Connected)
				{
					await _hubConnection.InvokeAsync("UnsubscribeFromProject", ProjectId.ToString());
					await _hubConnection.InvokeAsync("UnsubscribeFromJobList");
				}
				await _hubConnection.DisposeAsync();
			}
			catch
			{
			}
		}
	}
}
