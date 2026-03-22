using Microsoft.AspNetCore.Components;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.VersionControl;
using VibeSwarm.Shared.VersionControl.Models;

namespace VibeSwarm.Client.Pages;

public partial class JobDetail : ComponentBase
{
    // Uncommitted changes state
    private bool _isCheckingUncommittedChanges = false;
    private bool _hasCheckedUncommittedChanges = false;
    private bool _hasUncommittedChanges = false;
    private List<DiffFile> _uncommittedDiffFiles = new();
    private string? _uncommittedChangesError = null;
    private bool _isDiscardingChanges = false;
    private string _uncommittedCommitMessage = string.Empty;
    private bool _isCommittingUncommittedChanges = false;

    #region Uncommitted Changes

    private async Task CheckUncommittedChangesAsync()
    {
        if (Job?.Project?.WorkingPath == null || _isCheckingUncommittedChanges) return;

        _isCheckingUncommittedChanges = true;
        _uncommittedChangesError = null;
        _uncommittedDiffFiles.Clear();
        StateHasChanged();

        try
        {
            _hasUncommittedChanges = await VersionControlService.HasUncommittedChangesAsync(Job.Project.WorkingPath);

            if (_hasUncommittedChanges)
            {
                var diff = await VersionControlService.GetWorkingDirectoryDiffAsync(Job.Project.WorkingPath, null);
                if (!string.IsNullOrEmpty(diff))
                {
                    _uncommittedDiffFiles = GitDiffParser.ParseDiff(diff);

                    if (string.IsNullOrWhiteSpace(_uncommittedCommitMessage))
                    {
                        var prefix = Job.Status == JobStatus.Failed ? "WIP (partial):" : "WIP (cancelled):";
                        var goalSummary = Job.GoalPrompt.Length > 60 ? Job.GoalPrompt[..60] + "..." : Job.GoalPrompt;
                        _uncommittedCommitMessage = $"{prefix} {goalSummary}";
                    }
                }
            }

            _hasCheckedUncommittedChanges = true;
        }
        catch (Exception ex)
        {
            _uncommittedChangesError = $"Failed to check for uncommitted changes: {ex.Message}";
            Console.WriteLine($"Error checking uncommitted changes: {ex.Message}");
        }
        finally
        {
            _isCheckingUncommittedChanges = false;
            StateHasChanged();
        }
    }

    private async Task CommitUncommittedChangesAsync()
    {
        if (Job?.Project?.WorkingPath == null || string.IsNullOrWhiteSpace(_uncommittedCommitMessage) ||
            _isCommittingUncommittedChanges) return;

        _isCommittingUncommittedChanges = true;
        _uncommittedChangesError = null;
        StateHasChanged();

        try
        {
            var commitResult = await VersionControlService.CommitAllChangesAsync(Job.Project.WorkingPath,
                _uncommittedCommitMessage);

            if (commitResult.Success)
            {
                NotificationService.ShowSuccess($"Changes committed successfully: {commitResult.CommitHash?[..7] ?? "unknown"}",
                    "Committed");
                _hasUncommittedChanges = false;
                _uncommittedDiffFiles.Clear();
                _uncommittedCommitMessage = string.Empty;
                _hasCheckedUncommittedChanges = false;
            }
            else
            {
                _uncommittedChangesError = $"Failed to commit: {commitResult.Error}";
            }
        }
        catch (Exception ex)
        {
            _uncommittedChangesError = $"Failed to commit changes: {ex.Message}";
            Console.WriteLine($"Error committing uncommitted changes: {ex.Message}");
        }
        finally
        {
            _isCommittingUncommittedChanges = false;
            StateHasChanged();
        }
    }

    private async Task DiscardUncommittedChangesAsync()
    {
        if (Job?.Project?.WorkingPath == null || _isDiscardingChanges) return;

        _isDiscardingChanges = true;
        _uncommittedChangesError = null;
        StateHasChanged();

        try
        {
            var result = await VersionControlService.DiscardAllChangesAsync(Job.Project.WorkingPath);

            if (result.Success)
            {
                NotificationService.ShowSuccess("All uncommitted changes have been discarded.", "Changes Discarded");
                _hasUncommittedChanges = false;
                _uncommittedDiffFiles.Clear();
                _uncommittedCommitMessage = string.Empty;
                _hasCheckedUncommittedChanges = false;
            }
            else
            {
                _uncommittedChangesError = $"Failed to discard changes: {result.Error}";
            }
        }
        catch (Exception ex)
        {
            _uncommittedChangesError = $"Failed to discard changes: {ex.Message}";
            Console.WriteLine($"Error discarding uncommitted changes: {ex.Message}");
        }
        finally
        {
            _isDiscardingChanges = false;
            StateHasChanged();
        }
    }

    #endregion
}
