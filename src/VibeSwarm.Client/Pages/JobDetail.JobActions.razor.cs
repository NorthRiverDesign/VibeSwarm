using Microsoft.AspNetCore.Components;
using VibeSwarm.Client.Components.Jobs;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.VersionControl;

namespace VibeSwarm.Client.Pages;

public partial class JobDetail : ComponentBase
{
    // Job action loading state
    private bool IsCancelling { get; set; }
    private bool IsForceCancelling { get; set; }
    private bool IsRetrying { get; set; }
    private bool IsForceResetting { get; set; }

    // Retry modal state
    private bool _showRetryModal = false;
    private Idea? _linkedIdea;

	#region Job Actions

	private void ResetJobDetailState()
	{
		Job = null;
		_linkedIdea = null;
		_liveCommand = null;
		_showGitDiff = true;
		_parsedDiffFiles.Clear();
		_interactionChoices = null;
		_interactionError = null;
		_isSubmittingResponse = false;
		_showRetryModal = false;
		IsCancelling = false;
		IsForceCancelling = false;
		IsRetrying = false;
		IsForceResetting = false;

		lock (_liveOutput)
		{
			_liveOutput.Clear();
		}

		_pendingSessionMessages.Clear();
		_pendingOutputUpdate = false;

		_branchName = null;
		_isGitRepository = false;
		_showCreateBranchModal = false;
		_isCreatingBranch = false;
		_createBranchError = null;
		_isSyncingWithOrigin = false;
		_isRefreshingBranches = false;
		_isPruningBranches = false;
		_commitMessage = string.Empty;
		_isPushing = false;
		_isCheckingGitDiff = false;
		_isLoadingGitDiff = false;
		_isLoadingSummary = false;
		_summaryError = null;
		_pushStatus = "Pushing...";
		_pushError = null;
		_changesPushed = false;
		_pushedCommitHash = null;
		_isCreatingPullRequest = false;
		_isMergingBranch = false;
		_commitMessageInitialized = false;
		_isComparingWorkingCopy = false;
		_workingCopyComparisonDone = false;
		_workingCopyMatches = true;
		_workingCopyMissingFiles.Clear();
		_workingCopyExtraFiles.Clear();
		_workingCopyModifiedFiles.Clear();

		_pushCancellationTokenSource?.Cancel();
		_pushCancellationTokenSource?.Dispose();
		_pushCancellationTokenSource = null;

		_isCheckingUncommittedChanges = false;
		_hasCheckedUncommittedChanges = false;
		_hasUncommittedChanges = false;
		_uncommittedDiffFiles.Clear();
		_uncommittedChangesError = null;
		_isDiscardingChanges = false;
		_uncommittedCommitMessage = string.Empty;
		_isCommittingUncommittedChanges = false;
	}

	private async Task LoadJob()
	{
        try
        {
            Job = await JobService.GetByIdWithMessagesAsync(JobId);
            _linkedIdea = Job == null ? null : await IdeaService.GetByJobIdAsync(Job.Id);

            if (Job != null && !string.IsNullOrWhiteSpace(Job.SessionSummary))
            {
                if (string.IsNullOrWhiteSpace(_commitMessage) || !_commitMessageInitialized)
                {
                    _commitMessage = VibeSwarm.Shared.Services.JobSummaryGenerator.BuildCommitSubject(Job);
                    _commitMessageInitialized = true;
                    _isLoadingSummary = false;
                }
            }

            if (Job != null && !string.IsNullOrEmpty(Job.GitCommitHash))
            {
                _pushedCommitHash = Job.GitCommitHash;
                _changesPushed = !string.IsNullOrWhiteSpace(Job.PullRequestUrl)
                    || Job.MergedAt.HasValue
                    || Job.Project?.AutoCommitMode == AutoCommitMode.CommitAndPush
                    || Job.GitChangeDeliveryMode == GitChangeDeliveryMode.PullRequest;
            }

            // Populate live output buffer from persisted console output so refreshes can still
            // reconstruct the chat-style transcript even after the job has completed.
            if (Job != null && !string.IsNullOrEmpty(Job.ConsoleOutput))
            {
                lock (_liveOutput)
                {
                    if (!_liveOutput.Any())
                    {
                        var lines = Job.ConsoleOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                        foreach (var line in lines.TakeLast(MaxOutputLines))
                        {
                            _liveOutput.Add(JobSessionDisplayBuilder.CreateOutputLine(line, DateTime.UtcNow));
                        }
                    }
                }
            }

            if (Job != null && Job.Status == JobStatus.Paused && !string.IsNullOrEmpty(Job.InteractionChoices))
            {
                try { _interactionChoices = System.Text.Json.JsonSerializer.Deserialize<List<string>>(Job.InteractionChoices); }
                catch { _interactionChoices = null; }
            }
            else if (Job?.Status != JobStatus.Paused)
            {
                _interactionChoices = null;
            }

            if (Job != null && !string.IsNullOrEmpty(Job.GitDiff))
            {
                _parsedDiffFiles = GitDiffParser.ParseDiff(Job.GitDiff);

                if (string.IsNullOrEmpty(Job.GitCommitHash) && !_changesPushed && !_workingCopyComparisonDone &&
                    !IsJobActive && Job.Project?.WorkingPath != null)
                {
                    _ = CompareWithWorkingCopy();
                }
            }

            if (Job != null && ShowJobOutcomeSummary && _changeSets.Count == 0)
            {
                try { _changeSets = (await JobService.GetChangeSetsAsync(Job.Id)).ToList(); }
                catch { /* non-critical — page still works without change history */ }
            }

            ReconcilePendingSessionMessages();

            if (Job != null && (Job.Status == JobStatus.Failed || Job.Status == JobStatus.Cancelled) &&
                Job.Project?.WorkingPath != null && !_hasCheckedUncommittedChanges)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(500);
                        if (!_disposed) await InvokeAsync(async () => await CheckUncommittedChangesAsync());
                    }
                    catch { }
                });
            }
        }
        catch (Exception) when (!IsLoading)
        {
            // During refresh cycles, swallow the error so the existing page state is preserved.
            // The next poll or SignalR push will retry. Only throw during initial load so the
            // page can show a proper error state.
            return;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task CancelJob()
    {
        if (Job == null) return;

        IsCancelling = true;

        try
        {
            var result = await JobService.RequestCancellationAsync(Job.Id);
            if (result)
            {
                NotificationService.ShowProjectSuccess(Job.Project?.Name, "Cancellation requested. The job will be cancelled shortly.");
                await LoadJob();
            }
            else
            {
                NotificationService.ShowProjectError(Job.Project?.Name, "Could not cancel the job. It may have already completed.");
            }
        }
        catch (Exception ex)
        {
            NotificationService.ShowProjectError(Job.Project?.Name, $"Error cancelling job: {ex.Message}");
        }
        finally
        {
            IsCancelling = false;
        }
    }

    private async Task ForceCancelJob()
    {
        if (Job == null) return;

        IsForceCancelling = true;

        try
        {
            var result = await JobService.ForceCancelAsync(Job.Id);
            if (result)
            {
                NotificationService.ShowProjectSuccess(Job.Project?.Name, "Job has been force-cancelled.");
                await LoadJob();
            }
            else
            {
                NotificationService.ShowProjectError(Job.Project?.Name, "Could not force-cancel the job. It may have already completed.");
            }
        }
        catch (Exception ex)
        {
            NotificationService.ShowProjectError(Job.Project?.Name, $"Error force-cancelling job: {ex.Message}");
        }
        finally
        {
            IsForceCancelling = false;
        }
    }

    private async Task RetryJob()
    {
        if (Job == null) return;

        IsRetrying = true;

        try
        {
            var result = await JobService.ResetJobAsync(Job.Id);
            if (result)
            {
                NotificationService.ShowProjectSuccess(Job.Project?.Name, "Job has been reset and will be retried shortly.");
                await LoadJob();
            }
            else
            {
                NotificationService.ShowProjectError(Job.Project?.Name, "Could not retry the job. It may not be in a terminal state.");
            }
        }
        catch (Exception ex)
        {
            NotificationService.ShowProjectError(Job.Project?.Name, $"Error retrying job: {ex.Message}");
        }
        finally
        {
            IsRetrying = false;
        }
    }

    private async Task ForceResetJob()
    {
        if (Job == null) return;

        var confirmed = await JSRuntime.InvokeAsync<bool>("confirm",
            new object[] { "Are you sure you want to mark this job as Failed? This cannot be undone, but you can retry afterward." });
        if (!confirmed) return;

        IsForceResetting = true;

        try
        {
            var result = await JobService.ForceFailJobAsync(Job.Id);
            if (result)
            {
                NotificationService.ShowProjectSuccess(Job.Project?.Name, "Job has been marked as failed.");
                await LoadJob();
            }
            else
            {
                NotificationService.ShowProjectError(Job.Project?.Name, "Could not mark the job as failed. It may already be in a terminal state.");
            }
        }
        catch (Exception ex)
        {
            NotificationService.ShowProjectError(Job.Project?.Name, $"Error marking job as failed: {ex.Message}");
        }
        finally
        {
            IsForceResetting = false;
        }
    }

    private async Task ContinueJobWithFollowUp(string followUpPrompt)
    {
        if (Job == null || string.IsNullOrWhiteSpace(followUpPrompt)) return;

        var trimmedFollowUp = followUpPrompt.Trim();
        var submittedAt = DateTime.UtcNow;

        var optimisticMessage = new JobMessage
        {
            Id = Guid.NewGuid(),
            JobId = Job.Id,
            Role = MessageRole.User,
            Content = trimmedFollowUp,
            CreatedAt = submittedAt,
            Source = MessageSource.User,
            Level = MessageLevel.Normal
        };
        _pendingSessionMessages.Add(optimisticMessage);

        try
        {
            var result = await JobService.ContinueJobAsync(Job.Id, trimmedFollowUp);
            if (!result)
            {
                _pendingSessionMessages.Remove(optimisticMessage);
                NotificationService.ShowProjectError(Job.Project?.Name, "Could not continue the job. It may be in an active state.");
                return;
            }

            Job.Status = JobStatus.New;
            Job.CompletedAt = null;
            Job.CurrentActivity = "Queued follow-up instructions...";
            Job.LastActivityAt = submittedAt;

            NotificationService.ShowSuccess("Follow-up instructions queued. The job will continue shortly.", "Job Continued");
            await LoadJob();
        }
        catch (Exception ex)
        {
            _pendingSessionMessages.Remove(optimisticMessage);
            NotificationService.ShowProjectError(Job.Project?.Name, $"Failed to continue job: {ex.Message}");
        }
    }

    private void ShowRetryModal() => _showRetryModal = true;

    private async Task HandleRetryWithOptions(RetryJobModal.RetryOptions options)
    {
        if (Job == null) return;

        IsRetrying = true;
        _showRetryModal = false;

        try
        {
            var result = await JobService.ResetJobWithOptionsAsync(Job.Id, options.ProviderId, options.ModelId, options.ReasoningEffort);
            if (result)
            {
                NotificationService.ShowProjectSuccess(Job.Project?.Name, "Job has been reset with new options and will be retried shortly.");
                await LoadJob();
            }
            else
            {
                NotificationService.ShowProjectError(Job.Project?.Name, "Could not retry the job. It may not be in a terminal state or the provider is invalid.");
            }
        }
        catch (Exception ex)
        {
            NotificationService.ShowProjectError(Job.Project?.Name, $"Error retrying job: {ex.Message}");
        }
        finally
        {
            IsRetrying = false;
        }
    }

    #endregion
}
