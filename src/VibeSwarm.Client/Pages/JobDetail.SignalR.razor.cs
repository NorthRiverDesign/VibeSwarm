using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using VibeSwarm.Client.Components.Jobs;
using VibeSwarm.Shared.Data;

namespace VibeSwarm.Client.Pages;

public partial class JobDetail : ComponentBase, IAsyncDisposable
{
    private HubConnection? _hubConnection;
    private CancellationTokenSource? _signalRCts;
    private bool _signalRConnected = false;

    // Batches output-line re-renders to avoid calling StateHasChanged on every line,
    // which causes severe lag (100+ re-renders/sec) during verbose job output.
    private Timer? _outputRenderTimer;
    private volatile bool _pendingOutputUpdate = false;

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
        catch (OperationCanceledException) { }
        catch { }
    }

    private async Task InitializeSignalR(CancellationToken cancellationToken)
    {
        try
        {
            _hubConnection = new HubConnectionBuilder()
                .WithUrl(NavigationManager.ToAbsoluteUri("/hubs/job"))
                .WithAutomaticReconnect(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(10) })
                .Build();

            _hubConnection.On<string, string>("JobStatusChanged", async (jobId, status) =>
            {
                if (_disposed) return;
                try { await OnJobStatusChanged(jobId, status); }
                catch (ObjectDisposedException) { }
                catch { }
            });

            _hubConnection.On<string, string, DateTime>("JobActivityUpdated", async (jobId, activity, timestamp) =>
            {
                if (_disposed) return;
                try { await OnJobActivityUpdated(jobId, activity, timestamp); }
                catch (ObjectDisposedException) { }
                catch { }
            });

            _hubConnection.On<string>("JobMessageAdded", async (jobId) =>
            {
                if (_disposed) return;
                try { await OnJobMessageAdded(jobId); }
                catch (ObjectDisposedException) { }
                catch { }
            });

            _hubConnection.On<string, string, bool, DateTime>("JobOutput", async (jobId, line, isError, timestamp) =>
            {
                if (_disposed) return;
                try { await OnJobOutputReceived(jobId, line, isError, timestamp); }
                catch (ObjectDisposedException) { }
                catch { }
            });

            _hubConnection.On<string, int, string>("ProcessStarted", async (jobId, processId, command) =>
            {
                if (_disposed) return;
                try { await OnProcessStarted(jobId, processId, command); }
                catch (ObjectDisposedException) { }
                catch { }
            });

			_hubConnection.On<string, int, int, double>("ProcessExited", async (jobId, processId, exitCode, durationSeconds) =>
			{
				if (_disposed) return;
				if (!IsCurrentRouteJob(jobId)) return;
				try { await InvokeAsync(StateHasChanged); }
				catch (ObjectDisposedException) { }
				catch { }
			});

            _hubConnection.On<string, string, string, List<string>?, string?>("JobInteractionRequired",
                async (jobId, prompt, interactionType, choices, defaultResponse) =>
                {
                    if (_disposed) return;
                    try { await OnJobInteractionRequested(jobId, prompt, choices); }
                    catch (ObjectDisposedException) { }
                    catch { }
                });

            _hubConnection.On<string>("JobResumed", async (jobId) =>
            {
                if (_disposed) return;
                try { await OnJobInteractionCompleted(jobId); }
                catch (ObjectDisposedException) { }
                catch { }
            });

            _hubConnection.On<string, bool, string?>("JobCompleted", async (jobId, success, errorMessage) =>
            {
                if (_disposed) return;
                try { await OnJobCompleted(jobId, success, errorMessage); }
                catch (ObjectDisposedException) { }
                catch { }
            });

            _hubConnection.On<string, int, int>("JobCycleProgress", async (jobId, currentCycle, maxCycles) =>
            {
                if (_disposed) return;
                try { await OnJobCycleProgress(jobId, currentCycle, maxCycles); }
                catch (ObjectDisposedException) { }
                catch { }
            });

			_hubConnection.On<string, bool>("JobGitDiffUpdated", async (jobId, hasChanges) =>
			{
				if (_disposed) return;
				if (!IsCurrentRouteJob(jobId)) return;
				try
				{
					if (hasChanges)
                    {
                        await InvokeAsync(async () => await RefreshJobSafely());
                    }
                }
                catch (ObjectDisposedException) { }
                catch { }
            });

            _hubConnection.Reconnecting += _ =>
            {
                _signalRConnected = false;
                return Task.CompletedTask;
            };

            _hubConnection.Reconnected += async (connectionId) =>
            {
                _signalRConnected = true;
                if (!_disposed)
                {
                    try { await SubscribeToSignalRGroups(); }
                    catch { }
                }
            };

            _hubConnection.Closed += _ =>
            {
                _signalRConnected = false;
                return Task.CompletedTask;
            };

            await _hubConnection.StartAsync(cancellationToken);
            _signalRConnected = true;

            // Start batch render timer: flush pending output updates at most every 150ms
            // to avoid calling StateHasChanged on every individual output line.
            _outputRenderTimer = new Timer(async _ =>
            {
                if (_disposed) return;
                try
                {
                    if (_pendingOutputUpdate)
                    {
                        _pendingOutputUpdate = false;
                        await InvokeAsync(StateHasChanged);
                    }
                }
                catch (ObjectDisposedException) { }
                catch { }
            }, null, TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(150));

            await SubscribeToSignalRGroups();
        }
        catch (OperationCanceledException) { }
        catch { }
    }

	private async Task SubscribeToSignalRGroups()
	{
		if (_hubConnection?.State != HubConnectionState.Connected) return;

		try
        {
            await _hubConnection.InvokeAsync("SubscribeToJob", JobId.ToString());
            await _hubConnection.InvokeAsync("SubscribeToJobList");
		}
		catch { }
	}

	private async Task UnsubscribeFromJobAsync(Guid jobId)
	{
		if (_hubConnection?.State != HubConnectionState.Connected)
		{
			return;
		}

		try
		{
			await _hubConnection.InvokeAsync("UnsubscribeFromJob", jobId.ToString());
		}
		catch
		{
		}
	}

	private bool IsCurrentRouteJob(string jobId)
		=> Guid.TryParse(jobId, out var parsedJobId) && parsedJobId == JobId;

	#region SignalR Handlers

	private async Task OnJobStatusChanged(string jobId, string status)
	{
		if (!IsCurrentRouteJob(jobId) || Job == null)
		{
			return;
		}

		if (Job != null && Enum.TryParse<JobStatus>(status, out var newStatus))
		{
			Job.Status = newStatus;
            if (newStatus == JobStatus.Started)
            {
                Job.StartedAt = DateTime.UtcNow;
            }
            else if (newStatus == JobStatus.Completed || newStatus == JobStatus.Failed || newStatus == JobStatus.Cancelled)
            {
                Job.CompletedAt = DateTime.UtcNow;
                if (newStatus == JobStatus.Failed || newStatus == JobStatus.Cancelled)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(1000);
                            if (!_disposed) await InvokeAsync(async () => await CheckUncommittedChangesAsync());
                        }
                        catch { }
                    });
                }
            }
            await InvokeAsync(StateHasChanged);
        }

		if (!_isRefreshing && !_disposed)
		{
			_ = Task.Run(async () =>
			{
				try
				{
					await Task.Delay(500);
					if (!_disposed && IsCurrentRouteJob(jobId)) await InvokeAsync(async () => await RefreshJobSafely());
				}
				catch { }
			});
		}
	}

	private async Task OnJobActivityUpdated(string jobId, string activity, DateTime timestamp)
	{
		if (!IsCurrentRouteJob(jobId) || Job == null)
		{
			return;
		}

		if (Job != null)
		{
			Job.CurrentActivity = activity;
            Job.LastActivityAt = timestamp;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task OnJobMessageAdded(string jobId)
    {
        if (!_isRefreshing && !_disposed)
        {
            await InvokeAsync(async () => await RefreshJobSafely());
        }
    }

	private Task OnJobOutputReceived(string jobId, string line, bool isError, DateTime timestamp)
	{
		if (!IsCurrentRouteJob(jobId))
		{
			return Task.CompletedTask;
		}

		lock (_liveOutput)
		{
			_liveOutput.Add(JobSessionDisplayBuilder.CreateOutputLine(
                isError ? $"[ERR] {line}" : line,
                timestamp));
            // Trim from the front in one pass to avoid repeated O(n) RemoveAt(0) calls.
            if (_liveOutput.Count > MaxOutputLines)
            {
                var excess = _liveOutput.Count - MaxOutputLines;
                _liveOutput.RemoveRange(0, excess);
            }
        }
        // Signal the batch timer rather than calling StateHasChanged on every line.
        _pendingOutputUpdate = true;
        return Task.CompletedTask;
    }

	private async Task OnProcessStarted(string jobId, int processId, string command)
	{
		if (!IsCurrentRouteJob(jobId) || Job == null)
		{
			return;
		}

		if (Job != null)
		{
			_liveCommand = command;
            Job.ProcessId = processId;
            Job.CommandUsed = command;
            if (Job.Status == JobStatus.Planning)
            {
                Job.PlanningCommandUsed = command;
            }
            else
            {
                Job.ExecutionCommandUsed = command;
            }

            await InvokeAsync(StateHasChanged);
        }
    }

	private async Task OnJobInteractionRequested(string jobId, string prompt, List<string>? choices)
	{
		if (!IsCurrentRouteJob(jobId) || Job == null)
		{
			return;
		}

		if (Job != null)
		{
			Job.Status = JobStatus.Paused;
            Job.PendingInteractionPrompt = prompt;
            Job.InteractionRequestedAt = DateTime.UtcNow;
            _interactionChoices = choices;
            _interactionError = null;
            _isSubmittingResponse = false;
            await InvokeAsync(StateHasChanged);
        }
    }

	private async Task OnJobInteractionCompleted(string jobId)
	{
		if (!IsCurrentRouteJob(jobId) || Job == null)
		{
			return;
		}

		if (Job != null)
		{
			Job.Status = JobStatus.Processing;
            Job.PendingInteractionPrompt = null;
            Job.InteractionType = null;
            Job.InteractionRequestedAt = null;
            _interactionChoices = null;
            _interactionError = null;
            _isSubmittingResponse = false;
            await InvokeAsync(StateHasChanged);
        }
    }

	private async Task OnJobCycleProgress(string jobId, int currentCycle, int maxCycles)
	{
		if (!IsCurrentRouteJob(jobId) || Job == null)
		{
			return;
		}

		if (Job != null)
		{
			Job.CurrentCycle = currentCycle;
			Job.MaxCycles = maxCycles;
            await InvokeAsync(StateHasChanged);
        }
    }

	private async Task OnJobCompleted(string jobId, bool success, string? errorMessage)
	{
		if (!IsCurrentRouteJob(jobId) || Job == null)
		{
			return;
		}

		if (Job != null)
		{
			Job.Status = success ? JobStatus.Completed : JobStatus.Failed;
            Job.CompletedAt = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(errorMessage))
            {
                Job.ErrorMessage = errorMessage;
            }

            if (success)
            {
                _isLoadingGitDiff = true;
                _isLoadingSummary = true;
            }
            else
            {
                _ = Task.Run(async () =>
                {
					try
					{
						await Task.Delay(1000);
						if (!_disposed && IsCurrentRouteJob(jobId)) await InvokeAsync(async () => await CheckUncommittedChangesAsync());
					}
					catch { }
				});
            }
            await InvokeAsync(StateHasChanged);
        }

		if (!_disposed)
		{
			await Task.Delay(500);
			if (!IsCurrentRouteJob(jobId))
			{
				return;
			}

			await InvokeAsync(async () =>
			{
				await RefreshJobSafely();
				await HandlePostCompletionDataLoading(success);
            });
        }
    }

    private async Task HandlePostCompletionDataLoading(bool success)
    {
        try
        {
            var hasGitDiff = Job != null && !string.IsNullOrEmpty(Job.GitDiff);
            var hasSummary = Job != null && !string.IsNullOrWhiteSpace(Job.SessionSummary);

            if (hasGitDiff && hasSummary)
            {
                _isLoadingGitDiff = false;
                _isLoadingSummary = false;
                StateHasChanged();
                return;
            }

            var retryDelays = new[] { 1000, 2000, 3000 };
            foreach (var delay in retryDelays)
            {
                if (_disposed) return;
                await Task.Delay(delay);
                await RefreshJobSafely();

                hasGitDiff = Job != null && !string.IsNullOrEmpty(Job.GitDiff);
                hasSummary = Job != null && !string.IsNullOrWhiteSpace(Job.SessionSummary);

                if (hasGitDiff && hasSummary) break;

                if (hasGitDiff) _isLoadingGitDiff = false;
                if (hasSummary) _isLoadingSummary = false;
                StateHasChanged();
            }

            if (!hasGitDiff && success && Job?.Project?.WorkingPath != null && !_disposed)
            {
                try
                {
                    await CheckGitDiffAsync();
                    await RefreshJobSafely();
                }
                catch (Exception)
                {
                }
            }
        }
        catch (ObjectDisposedException) { }
        catch { }
        finally
        {
            _isLoadingGitDiff = false;
            _isLoadingSummary = false;
            if (!_disposed) StateHasChanged();
        }
    }

    #endregion

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _signalRConnected = false;
        _signalRCts?.Cancel();
        _signalRCts?.Dispose();
        _outputRenderTimer?.Dispose();
        _refreshTimer?.Dispose();
        _pushCancellationTokenSource?.Cancel();
        _pushCancellationTokenSource?.Dispose();

		if (_hubConnection != null)
		{
			try
			{
				if (_hubConnection.State == HubConnectionState.Connected)
				{
					await UnsubscribeFromJobAsync(JobId);
					await _hubConnection.InvokeAsync("UnsubscribeFromJobList");
				}
				await _hubConnection.DisposeAsync();
            }
            catch
            {
                // Ignore disposal errors
            }
        }
    }
}
