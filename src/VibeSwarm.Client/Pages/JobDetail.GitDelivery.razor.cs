using Microsoft.AspNetCore.Components;
using VibeSwarm.Shared.VersionControl;
using VibeSwarm.Shared.VersionControl.Models;

namespace VibeSwarm.Client.Pages;

public partial class JobDetail : ComponentBase
{
    // Git branch state
    private string? _branchName = null;
    private bool _isGitRepository = false;
    private bool _showCreateBranchModal = false;
    private bool _isCreatingBranch = false;
    private string? _createBranchError = null;
    private bool _isSyncingWithOrigin = false;
    private bool _isRefreshingBranches = false;
    private bool _isPruningBranches = false;

    // Commit and push state
    private string _commitMessage = string.Empty;
    private bool _isPushing = false;
    private bool _isCheckingGitDiff = false;
    private bool _isLoadingGitDiff = false;
    private bool _isLoadingSummary = false;
    private string? _summaryError = null;
    private string _pushStatus = "Pushing...";
    private string? _pushError = null;
    private bool _changesPushed = false;
    private string? _pushedCommitHash = null;
    private bool _isCreatingPullRequest = false;
    private bool _isMergingBranch = false;
    private CancellationTokenSource? _pushCancellationTokenSource;
    private const int PushTimeoutSeconds = 120;
    private bool _commitMessageInitialized = false;

    // Working copy comparison state
    private bool _isComparingWorkingCopy = false;
    private bool _workingCopyComparisonDone = false;
    private bool _workingCopyMatches = true;
    private List<string> _workingCopyMissingFiles = new();
    private List<string> _workingCopyExtraFiles = new();
    private List<string> _workingCopyModifiedFiles = new();

    #region Git Operations

    private async Task LoadBranchName()
    {
        try
        {
            if (Job?.Project?.WorkingPath != null)
            {
                _isGitRepository = await VersionControlService.IsGitRepositoryAsync(Job.Project.WorkingPath);
                if (_isGitRepository)
                {
                    _branchName = await VersionControlService.GetCurrentBranchAsync(Job.Project.WorkingPath);
                }
            }
        }
        catch
        {
            _isGitRepository = false;
        }
    }

    private void ShowCreateBranchModal()
    {
        if (!ShowGitActions || _isSyncingWithOrigin || _isRefreshingBranches || _isPruningBranches)
        {
            return;
        }

        _showCreateBranchModal = true;
        _createBranchError = null;
    }

    private async Task CreateBranch(string branchName)
    {
        if (string.IsNullOrWhiteSpace(branchName) || Job?.Project?.WorkingPath == null)
        {
            return;
        }

        _isCreatingBranch = true;
        _createBranchError = null;
        StateHasChanged();

        try
        {
            var trimmedBranchName = branchName.Trim();
            var result = await VersionControlService.CreateBranchAsync(Job.Project.WorkingPath, trimmedBranchName, switchToBranch: true);

            if (result.Success)
            {
                _branchName = result.BranchName ?? trimmedBranchName;
                _showCreateBranchModal = false;
                NotificationService.ShowSuccess($"Created and switched to branch '{_branchName}'.");
            }
            else
            {
                _createBranchError = result.Error ?? "Failed to create branch.";
            }
        }
        catch (Exception ex)
        {
            _createBranchError = $"Error creating branch: {ex.Message}";
        }
        finally
        {
            _isCreatingBranch = false;
            StateHasChanged();
        }
    }

    private async Task SyncWithOrigin()
    {
        if (_isSyncingWithOrigin || Job?.Project?.WorkingPath == null)
        {
            return;
        }

        _isSyncingWithOrigin = true;
        StateHasChanged();

        try
        {
            var result = await VersionControlService.SyncWithOriginAsync(Job.Project.WorkingPath);
            if (result.Success)
            {
                await LoadBranchName();
                NotificationService.ShowSuccess(string.IsNullOrWhiteSpace(_branchName)
                    ? "Successfully synced with origin."
                    : $"Successfully synced with origin/{_branchName}.");
            }
            else
            {
                NotificationService.ShowError(result.Error ?? "Failed to sync with origin.");
            }
        }
        catch (Exception ex)
        {
            NotificationService.ShowError($"Error syncing with origin: {ex.Message}");
        }
        finally
        {
            _isSyncingWithOrigin = false;
            StateHasChanged();
        }
    }

    private async Task RefreshBranches()
    {
        if (_isRefreshingBranches || Job?.Project?.WorkingPath == null)
        {
            return;
        }

        _isRefreshingBranches = true;
        StateHasChanged();

        try
        {
            await VersionControlService.FetchAsync(Job.Project.WorkingPath);
            await LoadBranchName();
            NotificationService.ShowSuccess("Branch information refreshed.");
        }
        catch (Exception ex)
        {
            NotificationService.ShowError($"Failed to refresh branches: {ex.Message}");
        }
        finally
        {
            _isRefreshingBranches = false;
            StateHasChanged();
        }
    }

    private async Task PruneRemoteBranches()
    {
        if (_isPruningBranches || Job?.Project?.WorkingPath == null)
        {
            return;
        }

        _isPruningBranches = true;
        StateHasChanged();

        try
        {
            var result = await VersionControlService.PruneRemoteBranchesAsync(Job.Project.WorkingPath);
            if (result.Success)
            {
                await LoadBranchName();
                NotificationService.ShowSuccess("Stale remote branches pruned successfully.");
            }
            else
            {
                NotificationService.ShowError(result.Error ?? "Failed to prune stale branches.");
            }
        }
        catch (Exception ex)
        {
            NotificationService.ShowError($"Error pruning stale branches: {ex.Message}");
        }
        finally
        {
            _isPruningBranches = false;
            StateHasChanged();
        }
    }

    private async Task RefreshSummary()
    {
        if (Job == null) return;

        _isLoadingSummary = true;
        _summaryError = null;
        StateHasChanged();

        try
        {
            await RefreshJobSafely();

            if (Job != null && string.IsNullOrWhiteSpace(Job.SessionSummary) && !string.IsNullOrEmpty(Job.GitDiff))
            {
                var localSummary = VibeSwarm.Shared.Services.JobSummaryGenerator.GenerateSummary(Job);
                if (!string.IsNullOrWhiteSpace(localSummary))
                {
                    _commitMessage = localSummary;
                    _commitMessageInitialized = true;
                }
                else
                {
                    _summaryError = "Unable to generate a summary. Please write your own commit message.";
                }
            }
        }
        catch (Exception)
        {
            _summaryError = "Failed to refresh summary. Please try again or write your own commit message.";
        }
        finally
        {
            _isLoadingSummary = false;
            StateHasChanged();
        }
    }

    private async Task CheckGitDiffAsync()
    {
        if (Job?.Project?.WorkingPath == null || _isCheckingGitDiff) return;

        _isCheckingGitDiff = true;
        StateHasChanged();

        try
        {
            var gitDiff = await VersionControlService.GetWorkingDirectoryDiffAsync(Job.Project.WorkingPath, Job.GitCommitBefore);
            await JobService.UpdateGitDiffAsync(Job.Id, gitDiff);
            await RefreshJobSafely();

            if (!string.IsNullOrEmpty(gitDiff) && string.IsNullOrWhiteSpace(_commitMessage))
            {
                var localSummary = VibeSwarm.Shared.Services.JobSummaryGenerator.GenerateSummary(gitDiff, Job.GoalPrompt,
                    Job.ConsoleOutput);
                if (!string.IsNullOrWhiteSpace(localSummary))
                {
                    _commitMessage = localSummary;
                    _commitMessageInitialized = true;
                }
            }
        }
        catch (Exception ex)
        {
            NotificationService.ShowError($"Error checking git changes: {ex.Message}");
        }
        finally
        {
            _isCheckingGitDiff = false;
            StateHasChanged();
        }
    }

    private async Task CompareWithWorkingCopy()
    {
        if (Job?.Project?.WorkingPath == null || string.IsNullOrEmpty(Job.GitDiff) || _isComparingWorkingCopy) return;

        _isComparingWorkingCopy = true;
        _workingCopyMissingFiles.Clear();
        _workingCopyExtraFiles.Clear();
        _workingCopyModifiedFiles.Clear();
        StateHasChanged();

        try
        {
            var currentDiff = await VersionControlService.GetWorkingDirectoryDiffAsync(
                Job.Project.WorkingPath, Job.GitCommitBefore);

            if (string.IsNullOrEmpty(currentDiff) && !string.IsNullOrEmpty(Job.GitCommitBefore))
            {
                currentDiff = await VersionControlService.GetCommitRangeDiffAsync(
                    Job.Project.WorkingPath, Job.GitCommitBefore);
            }

            var jobFiles = GitDiffParser.ParseDiff(Job.GitDiff);
            var currentFiles = GitDiffParser.ParseDiff(currentDiff ?? string.Empty);

            var (missing, extra, modified) = GitDiffParser.CompareDiffs(jobFiles, currentFiles);
            _workingCopyMissingFiles = missing;
            _workingCopyExtraFiles = extra;
            _workingCopyModifiedFiles = modified;

            _workingCopyMatches = !missing.Any() && !extra.Any() && !modified.Any();
            _workingCopyComparisonDone = true;
        }
        catch (Exception)
        {
            _workingCopyComparisonDone = true;
            _workingCopyMatches = false;
        }
        finally
        {
            _isComparingWorkingCopy = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task CommitAndPushChanges()
    {
        if (Job?.Project?.WorkingPath == null || string.IsNullOrWhiteSpace(_commitMessage)) return;

        _isPushing = true;
        _pushError = null;
        _pushStatus = "Checking for changes...";
        StateHasChanged();

        _pushCancellationTokenSource?.Cancel();
        _pushCancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(PushTimeoutSeconds));

        try
        {
            var hasUncommitted = await VersionControlService.HasUncommittedChangesAsync(Job.Project.WorkingPath,
                _pushCancellationTokenSource.Token);

            GitOperationResult result;

            if (hasUncommitted)
            {
                _pushStatus = "Committing and pushing...";
                StateHasChanged();

                result = await VersionControlService.CommitAndPushAsync(
                    Job.Project.WorkingPath,
                    _commitMessage,
                    "origin",
                    status => { _pushStatus = status; InvokeAsync(StateHasChanged); },
                    _pushCancellationTokenSource.Token);
            }
            else
            {
                _pushStatus = "Changes already committed. Pushing...";
                StateHasChanged();

                result = await VersionControlService.PushAsync(
                    Job.Project.WorkingPath,
                    "origin",
                    null,
                    _pushCancellationTokenSource.Token);

                if (result.Success)
                {
                    var currentHash = await VersionControlService.GetCurrentCommitHashAsync(Job.Project.WorkingPath,
                        _pushCancellationTokenSource.Token);
                    result = new GitOperationResult
                    {
                        Success = true,
                        CommitHash = currentHash,
                        BranchName = result.BranchName,
                        RemoteName = result.RemoteName,
                        Output = result.Output
                    };
                }
            }

            if (result.Success)
            {
                _changesPushed = true;
                _pushedCommitHash = result.CommitHash;
                _branchName = result.BranchName ?? _branchName;
                NotificationService.ShowSuccess("Changes committed and pushed successfully!");

                if (!string.IsNullOrEmpty(result.CommitHash) && Job != null)
                {
                    await JobService.UpdateGitCommitHashAsync(Job.Id, result.CommitHash);
                    Job.GitCommitHash = result.CommitHash;
                }
            }
            else
            {
                _pushError = result.Error ?? "An unknown error occurred";
                NotificationService.ShowError(_pushError);
            }
        }
        catch (OperationCanceledException)
        {
            _pushError = "Push operation timed out. Please check your network connection and try again.";
            NotificationService.ShowError(_pushError);
        }
        catch (Exception ex)
        {
            _pushError = $"Unexpected error: {ex.Message}";
            NotificationService.ShowError(_pushError);
        }
        finally
        {
            _isPushing = false;
            _pushStatus = "Pushing...";
            StateHasChanged();
        }
    }

    private async Task CreatePullRequest()
    {
        if (Job?.Project?.WorkingPath == null || !CanCreatePullRequest || string.IsNullOrWhiteSpace(EffectiveTargetBranch) || string.IsNullOrWhiteSpace(_branchName))
        {
            return;
        }

        _isCreatingPullRequest = true;
        _pushError = null;
        StateHasChanged();

        try
        {
            if (!_changesPushed)
            {
                await CommitAndPushChanges();
                if (!_changesPushed || Job == null)
                {
                    return;
                }
            }

            var prTitle = VibeSwarm.Shared.Services.JobSummaryGenerator.BuildCommitSubject(Job);
            var prBody = BuildPullRequestBody();
            var result = await VersionControlService.CreatePullRequestAsync(
                Job.Project.WorkingPath,
                _branchName,
                EffectiveTargetBranch,
                prTitle,
                prBody);

            if (!result.Success)
            {
                _pushError = result.Error ?? "Failed to create pull request.";
                NotificationService.ShowError(_pushError);
                return;
            }

            Job.PullRequestNumber = result.PullRequestNumber;
            Job.PullRequestUrl = result.PullRequestUrl;
            Job.PullRequestCreatedAt = DateTime.UtcNow;
            _changesPushed = true;

            await JobService.UpdateGitDeliveryAsync(
                Job.Id,
                commitHash: Job.GitCommitHash,
                pullRequestNumber: result.PullRequestNumber,
                pullRequestUrl: result.PullRequestUrl,
                pullRequestCreatedAt: Job.PullRequestCreatedAt);

            NotificationService.ShowSuccess(result.PullRequestUrl is null
                ? "Pull request created successfully."
                : $"Pull request created: {result.PullRequestUrl}");
        }
        catch (Exception ex)
        {
            _pushError = $"Error creating pull request: {ex.Message}";
            NotificationService.ShowError(_pushError);
        }
        finally
        {
            _isCreatingPullRequest = false;
            StateHasChanged();
        }
    }

    private async Task MergeBranchIntoTarget()
    {
        if (Job?.Project?.WorkingPath == null || !CanMergeBranch || string.IsNullOrWhiteSpace(EffectiveTargetBranch) || string.IsNullOrWhiteSpace(_branchName))
        {
            return;
        }

        _isMergingBranch = true;
        _pushError = null;
        StateHasChanged();

        try
        {
            var result = await VersionControlService.MergeBranchAsync(
                Job.Project.WorkingPath,
                _branchName,
                EffectiveTargetBranch,
                "origin",
                progress =>
                {
                    _pushStatus = progress;
                    InvokeAsync(StateHasChanged);
                });

            if (!result.Success)
            {
                _pushError = result.Error ?? "Failed to merge branch.";
                NotificationService.ShowError(_pushError);
                return;
            }

            Job.MergedAt = DateTime.UtcNow;
            Job.GitCommitHash = result.CommitHash ?? Job.GitCommitHash;
            _pushedCommitHash = Job.GitCommitHash;
            _changesPushed = true;
            _branchName = result.BranchName ?? EffectiveTargetBranch;

            await JobService.UpdateGitDeliveryAsync(
                Job.Id,
                commitHash: result.CommitHash,
                mergedAt: Job.MergedAt);

            NotificationService.ShowSuccess($"Merged '{Job.Branch ?? _branchName}' into '{EffectiveTargetBranch}'.");
            await LoadBranchName();
        }
        catch (Exception ex)
        {
            _pushError = $"Error merging branch: {ex.Message}";
            NotificationService.ShowError(_pushError);
        }
        finally
        {
            _isMergingBranch = false;
            _pushStatus = "Pushing...";
            StateHasChanged();
        }
    }

    private string BuildPullRequestBody()
    {
        if (Job == null || string.IsNullOrWhiteSpace(_branchName) || string.IsNullOrWhiteSpace(EffectiveTargetBranch))
        {
            return string.Empty;
        }

        var lines = new List<string>
        {
            "## VibeSwarm Job",
            string.Empty,
            $"- Source branch: `{_branchName}`",
            $"- Target branch: `{EffectiveTargetBranch}`",
            string.Empty,
            "### Goal",
            Job.GoalPrompt.Trim()
        };

        if (!string.IsNullOrWhiteSpace(Job.Title))
        {
            lines.Insert(4, $"- Job: `{Job.Title}`");
        }

        if (!string.IsNullOrWhiteSpace(Job.SessionSummary))
        {
            lines.Add(string.Empty);
            lines.Add("### Session Summary");
            lines.Add(Job.SessionSummary.Trim());
        }

        return string.Join("\n", lines);
    }

    #endregion
}
