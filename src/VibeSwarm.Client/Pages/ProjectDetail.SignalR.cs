using Microsoft.AspNetCore.SignalR.Client;
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

	private static readonly TimeSpan PollingIntervalActive = TimeSpan.FromSeconds(10);
	private static readonly TimeSpan PollingIntervalSignalRFallback = TimeSpan.FromSeconds(30);
	private bool IsSignalRConnected => _hubConnection?.State == HubConnectionState.Connected;

	protected override async Task OnInitializedAsync()
	{
		await LoadData();

		_ = LoadGitInfoSafe();
		_ = InitializeSignalRSafe();
		StartAutoRefresh();
	}

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
		.WithAutomaticReconnect(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5),
TimeSpan.FromSeconds(10) })
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
					var job = Jobs.FirstOrDefault(j => j.Id.ToString() == jobId);
					if (job != null)
					{
						job.CurrentActivity = activity;
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
					ProcessingIdeaIds.Clear();

					if (success && (_activeTab == "changes" || _hasUncommittedChangesHeader))
					{
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
					if (!Jobs.Any(j => j.Id.ToString() == jobId))
					{
						return;
					}

					await RefreshJobs();
					await RefreshUncommittedChangesStatus();

					if (hasChanges || _activeTab == "changes")
					{
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
						try { _autoPilotStatus = await AutoPilotService.GetStatusAsync(ProjectId); } catch { }
						if (_autoPilotPanel != null)
							await _autoPilotPanel.RefreshAsync();
						StateHasChanged();
					}
				});
			}
			catch (ObjectDisposedException) { }
			catch (Exception) { }
		});
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
				if (HasActiveJobs || IsIdeasProcessingActive)
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
						StateHasChanged();
					});
				}
				else if (_activeTab == "changes" && IsGitRepository)
				{
					await InvokeAsync(async () =>
					{
						if (_disposed) return;
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
		IsLoading = true;
		try
		{
			Project = await ProjectService.GetByIdAsync(ProjectId);
			if (Project == null) return;

			IsLoading = false;
			_isLoadingJobs = true;
			_isLoadingIdeas = true;
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
			var defaultProviderTask = LoadDefaultProvider();
			var inferenceTask = LoadInferenceModels();

			await Task.WhenAll(suggestionTask, defaultProviderTask, inferenceTask);
		}
		catch
		{
			Providers = new List<Provider>();
		}
	}

	private async Task LoadDefaultProvider()
	{
		var defaultProvider = await ProviderService.GetDefaultAsync();
		if (defaultProvider != null)
		{
			NewJob.ProviderId = defaultProvider.Id;
			await LoadModelsForProvider(defaultProvider.Id);
		}
	}

	public async ValueTask DisposeAsync()
	{
		_disposed = true;

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
