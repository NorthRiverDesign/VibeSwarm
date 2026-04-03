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
                    _commitMessage = Job.SessionSummary;
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
                NotificationService.ShowSuccess("Cancellation requested. The job will be cancelled shortly.");
                await LoadJob();
            }
            else
            {
                NotificationService.ShowError("Could not cancel the job. It may have already completed.");
            }
        }
        catch (Exception ex)
        {
            NotificationService.ShowError($"Error cancelling job: {ex.Message}");
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
                NotificationService.ShowSuccess("Job has been force-cancelled.");
                await LoadJob();
            }
            else
            {
                NotificationService.ShowError("Could not force-cancel the job. It may have already completed.");
            }
        }
        catch (Exception ex)
        {
            NotificationService.ShowError($"Error force-cancelling job: {ex.Message}");
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
                NotificationService.ShowSuccess("Job has been reset and will be retried shortly.");
                await LoadJob();
            }
            else
            {
                NotificationService.ShowError("Could not retry the job. It may not be in a terminal state.");
            }
        }
        catch (Exception ex)
        {
            NotificationService.ShowError($"Error retrying job: {ex.Message}");
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
                NotificationService.ShowSuccess("Job has been marked as failed.");
                await LoadJob();
            }
            else
            {
                NotificationService.ShowError("Could not mark the job as failed. It may already be in a terminal state.");
            }
        }
        catch (Exception ex)
        {
            NotificationService.ShowError($"Error marking job as failed: {ex.Message}");
        }
        finally
        {
            IsForceResetting = false;
        }
    }

    private async Task ContinueJobWithFollowUp(string followUpPrompt)
    {
        if (Job == null || string.IsNullOrWhiteSpace(followUpPrompt)) return;

        try
        {
            var trimmedFollowUp = followUpPrompt.Trim();
            var submittedAt = DateTime.UtcNow;
            var result = await JobService.ContinueJobAsync(Job.Id, trimmedFollowUp);
            if (!result)
            {
                NotificationService.ShowError("Could not continue the job. It may no longer be completed.");
                return;
            }

            _pendingSessionMessages.Add(new JobMessage
            {
                Id = Guid.NewGuid(),
                JobId = Job.Id,
                Role = MessageRole.User,
                Content = trimmedFollowUp,
                CreatedAt = submittedAt,
                Source = MessageSource.User,
                Level = MessageLevel.Normal
            });

            Job.Status = JobStatus.New;
            Job.CompletedAt = null;
            Job.CurrentActivity = "Queued follow-up instructions...";
            Job.LastActivityAt = submittedAt;

            NotificationService.ShowSuccess("Follow-up instructions queued. The job will continue shortly.", "Job Continued");
            await LoadJob();
        }
        catch (Exception ex)
        {
            NotificationService.ShowError($"Failed to continue job: {ex.Message}", "Error");
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
                NotificationService.ShowSuccess("Job has been reset with new options and will be retried shortly.");
                await LoadJob();
            }
            else
            {
                NotificationService.ShowError("Could not retry the job. It may not be in a terminal state or the provider is invalid.");
            }
        }
        catch (Exception ex)
        {
            NotificationService.ShowError($"Error retrying job: {ex.Message}");
        }
        finally
        {
            IsRetrying = false;
        }
    }

    #endregion
}
