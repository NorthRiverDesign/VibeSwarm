using VibeSwarm.Client.Models;
using VibeSwarm.Shared;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.VersionControl;
using VibeSwarm.Shared.VersionControl.Models;

namespace VibeSwarm.Client.Pages;

public partial class ProjectDetail
{
    // Lazy-loaded jobs for Changes tab and Uncommitted Changes modal.
    // Only loaded on-demand when these features are used, not on every refresh.
    private List<Job>? _changesJobs;
    private DateTime _lastChangesJobsLoadTime = DateTime.MinValue;
    private static readonly TimeSpan ChangesJobsDebounceInterval = TimeSpan.FromSeconds(5);

    private async Task EnsureChangesJobsLoadedAsync(bool force = false)
    {
        if (!force && _changesJobs != null && (DateTime.UtcNow - _lastChangesJobsLoadTime) < ChangesJobsDebounceInterval)
            return;

        try
        {
            _changesJobs = (await JobService.GetByProjectIdAsync(ProjectId)).ToList();
            _lastChangesJobsLoadTime = DateTime.UtcNow;
        }
        catch
        {
            _changesJobs ??= new List<Job>();
        }
    }

    private async Task LoadGitInfo()
    {
        if (Project == null || string.IsNullOrEmpty(Project.WorkingPath))
        {
            _isLoadingGitInfo = false;
            return;
        }

        _isLoadingGitInfo = true;

        try
        {
            IsGitRepository = await VersionControlService.IsGitRepositoryAsync(Project.WorkingPath);

            if (IsGitRepository)
            {
                CurrentBranch = await VersionControlService.GetCurrentBranchAsync(Project.WorkingPath);
                CurrentCommitHash = await VersionControlService.GetCurrentCommitHashAsync(Project.WorkingPath);
                NewJob.Branch = CurrentBranch;
                await LoadBranches();
                await RefreshUncommittedChangesStatus();
            }
            else
            {
                _workingTreeStatus = new GitWorkingTreeStatus();
            }
        }
        catch
        {
            IsGitRepository = false;
            _workingTreeStatus = new GitWorkingTreeStatus();
        }
        finally
        {
            _isLoadingGitInfo = false;
        }
    }

    private async Task RefreshUncommittedChangesStatus()
    {
        if (Project == null || string.IsNullOrEmpty(Project.WorkingPath) || !IsGitRepository)
            return;

        try
        {
            _workingTreeStatus = await VersionControlService.GetWorkingTreeStatusAsync(Project.WorkingPath);
            _hasUncommittedChangesHeader = _workingTreeStatus.HasUncommittedChanges;
            _uncommittedFilesCount = _workingTreeStatus.ChangedFilesCount;
        }
        catch
        {
            _workingTreeStatus = new GitWorkingTreeStatus();
            _hasUncommittedChangesHeader = false;
            _uncommittedFilesCount = 0;
        }
    }

    private async Task LoadBranches()
    {
        if (Project == null || string.IsNullOrEmpty(Project.WorkingPath))
            return;

        try
        {
            Branches = (await VersionControlService.GetBranchesAsync(Project.WorkingPath, includeRemote: true)).ToList();
        }
        catch
        {
            Branches = new List<GitBranchInfo>();
        }
    }

    private async Task RefreshBranches()
    {
        if (IsGitOperationInProgress) return;

        IsGitOperationInProgress = true;
        GitProgressMessage = "Fetching latest branches...";
        StateHasChanged();

        try
        {
            if (Project != null && !string.IsNullOrEmpty(Project.WorkingPath))
            {
                await VersionControlService.FetchAsync(Project.WorkingPath);
                await LoadBranches();
                CurrentBranch = await VersionControlService.GetCurrentBranchAsync(Project.WorkingPath);
                CurrentCommitHash = await VersionControlService.GetCurrentCommitHashAsync(Project.WorkingPath);
            }
        }
        catch (Exception ex)
        {
            GitOperationMessage = $"Failed to refresh branches: {ex.Message}";
            GitOperationSuccess = false;
        }
        finally
        {
            IsGitOperationInProgress = false;
            GitProgressMessage = null;
            StateHasChanged();
        }
    }

    private async Task RequestBranchSwitch(string targetBranch)
    {
        if (string.IsNullOrEmpty(targetBranch) || targetBranch == CurrentBranch)
            return;

        _targetBranch = targetBranch;
        _showBranchSwitchModal = true;
        _branchSwitchError = null;
        _isCheckingUncommittedChanges = true;
        _hasUncommittedChanges = false;
        StateHasChanged();

        if (Project != null && !string.IsNullOrEmpty(Project.WorkingPath))
        {
            try
            {
                _hasUncommittedChanges = await VersionControlService.HasUncommittedChangesAsync(Project.WorkingPath);
            }
            catch
            {
                _hasUncommittedChanges = false;
            }
        }

        _isCheckingUncommittedChanges = false;
        StateHasChanged();
    }

    private void CloseBranchSwitchModal()
    {
        _showBranchSwitchModal = false;
        _targetBranch = null;
        _branchSwitchError = null;
    }

    private async Task ShowMergeBranchModal()
    {
        if (IsGitOperationInProgress || Project == null || string.IsNullOrWhiteSpace(Project.WorkingPath) || !HasMergeBranchTargets)
        {
            return;
        }

        _mergePushAfterMerge = true;
        _mergeCreatePullRequest = false;
        _mergePrTitle = null;
        _isCreatingPullRequest = false;
        _mergePreviewError = null;
        _mergePreviewMessage = null;
        _isMergeAlreadyUpToDate = false;
        _mergeConflictFiles.Clear();
        _mergeTargetBranch = SelectDefaultMergeTargetBranch();
        _showMergeBranchModal = true;
        StateHasChanged();

        _ = CheckGitHubCliAvailabilityAsync();
        await PreviewMergeBranchAsync();
    }

    private void CloseMergeBranchModal()
    {
        _showMergeBranchModal = false;
        _mergeTargetBranch = null;
        _mergePushAfterMerge = true;
        _mergeCreatePullRequest = false;
        _mergePrTitle = null;
        _isCreatingPullRequest = false;
        _mergePreviewMessage = null;
        _mergePreviewError = null;
        _isMergeAlreadyUpToDate = false;
        _mergeConflictFiles.Clear();
    }

    private async Task HandleMergeTargetBranchChanged(string targetBranch)
    {
        _mergeTargetBranch = targetBranch;
        await PreviewMergeBranchAsync();
    }

    private Task HandleMergePushAfterMergeChanged(bool pushAfterMerge)
    {
        _mergePushAfterMerge = pushAfterMerge;
        return Task.CompletedTask;
    }

    private Task HandleMergeCreatePullRequestChanged(bool createPr)
    {
        _mergeCreatePullRequest = createPr;
        if (createPr)
        {
            _mergePushAfterMerge = true;
        }
        return Task.CompletedTask;
    }

    private Task HandleMergePrTitleChanged(string? title)
    {
        _mergePrTitle = title;
        return Task.CompletedTask;
    }

    private Task HandleMergeConflictResolutionChanged(MergeConflictResolution resolution)
    {
        var conflictFile = _mergeConflictFiles.FirstOrDefault(file => string.Equals(file.FileName, resolution.FileName, StringComparison.Ordinal));
        if (conflictFile is not null)
        {
            conflictFile.Content = resolution.ResolvedContent;
        }

        return Task.CompletedTask;
    }

    private async Task CheckGitHubCliAvailabilityAsync()
    {
        try
        {
            _isGitHubCliAvailable = await VersionControlService.IsGitHubCliAvailableAsync();
            if (_isGitHubCliAvailable)
            {
                _isGitHubCliAvailable = await VersionControlService.IsGitHubCliAuthenticatedAsync();
            }
            StateHasChanged();
        }
        catch
        {
            _isGitHubCliAvailable = false;
        }
    }

    private string? SelectDefaultMergeTargetBranch()
    {
        var preferredBranch = Project?.DefaultTargetBranch;
        if (!string.IsNullOrWhiteSpace(preferredBranch) &&
            MergeTargetBranches.Contains(preferredBranch, StringComparer.Ordinal))
        {
            return preferredBranch;
        }

        return MergeTargetBranches.FirstOrDefault();
    }

    private async Task PreviewMergeBranchAsync()
    {
        if (!_showMergeBranchModal || _isMergingBranch || Project == null || string.IsNullOrWhiteSpace(Project.WorkingPath) ||
            string.IsNullOrWhiteSpace(CurrentBranch) || string.IsNullOrWhiteSpace(_mergeTargetBranch))
        {
            return;
        }

        _isCheckingMergePreview = true;
        _mergePreviewMessage = null;
        _mergePreviewError = null;
        _isMergeAlreadyUpToDate = false;
        _mergeConflictFiles.Clear();
        StateHasChanged();

        try
        {
            var result = await VersionControlService.PreviewMergeBranchAsync(
                Project.WorkingPath,
                CurrentBranch,
                _mergeTargetBranch);

            if (result.Success)
            {
                _isMergeAlreadyUpToDate = result.ChangedFilesCount == 0;
                _mergePreviewMessage = result.Output ?? $"'{CurrentBranch}' can be merged into '{_mergeTargetBranch}' without conflicts.";
                _mergeConflictFiles = new List<MergeConflictFile>();
            }
            else
            {
                _mergePreviewError = result.Error ?? $"{AppConstants.AppName} could not verify the merge preview.";
                _mergeConflictFiles = result.MergeConflictFiles.Select(CloneMergeConflictFile).ToList();
                if (_mergeConflictFiles.Count > 0)
                {
                    _mergeCreatePullRequest = false;
                }
            }
        }
        catch (Exception ex)
        {
            _mergePreviewError = $"Error checking merge preview: {ex.Message}";
        }
        finally
        {
            _isCheckingMergePreview = false;
            StateHasChanged();
        }
    }

    private async Task MergeCurrentBranchAsync((string targetBranch, bool pushAfterMerge) args)
    {
        if (IsGitOperationInProgress || Project == null || string.IsNullOrWhiteSpace(Project.WorkingPath) ||
            string.IsNullOrWhiteSpace(CurrentBranch) || string.IsNullOrWhiteSpace(args.targetBranch))
        {
            return;
        }

        IsGitOperationInProgress = true;
        _isMergingBranch = true;
        _mergeTargetBranch = args.targetBranch;
        _mergePushAfterMerge = args.pushAfterMerge;
        _mergePreviewError = null;
        _mergePreviewMessage = null;
        GitOperationMessage = null;
        GitProgressMessage = $"Preparing to merge '{CurrentBranch}' into '{args.targetBranch}'...";
        StateHasChanged();

        try
        {
            if (_mergeConflictFiles.Count == 0)
            {
                var previewResult = await VersionControlService.PreviewMergeBranchAsync(
                    Project.WorkingPath,
                    CurrentBranch,
                    args.targetBranch);

                if (!previewResult.Success)
                {
                    _mergePreviewError = previewResult.Error ?? $"{AppConstants.AppName} could not verify the merge preview.";
                    _mergeConflictFiles = previewResult.MergeConflictFiles.Select(CloneMergeConflictFile).ToList();
                    return;
                }

                _mergePreviewMessage = previewResult.Output ?? $"'{CurrentBranch}' can be merged into '{args.targetBranch}' without conflicts.";
            }

            var result = await VersionControlService.MergeBranchAsync(
                Project.WorkingPath,
                CurrentBranch,
                args.targetBranch,
                progressCallback: progress =>
                {
                    GitProgressMessage = progress;
                    InvokeAsync(StateHasChanged);
                },
                pushAfterMerge: args.pushAfterMerge,
                conflictResolutions: _mergeConflictFiles.Count > 0
                    ? _mergeConflictFiles.Select(file => new MergeConflictResolution
                    {
                        FileName = file.FileName,
                        ResolvedContent = file.Content
                    }).ToList()
                    : null);

            if (!result.Success)
            {
                _mergePreviewError = result.Error ?? "Failed to merge branches.";
                _mergeConflictFiles = result.MergeConflictFiles.Select(CloneMergeConflictFile).ToList();
                return;
            }

            await LoadBranches();
            CloseMergeBranchModal();

            NotificationService.ShowProjectSuccess(
                Project.Name,
                result.Output ?? $"Merged '{CurrentBranch}' into '{args.targetBranch}'.");
        }
        catch (Exception ex)
        {
            _mergePreviewError = $"Error merging branches: {ex.Message}";
        }
        finally
        {
            _isMergingBranch = false;
            IsGitOperationInProgress = false;
            GitProgressMessage = null;
            StateHasChanged();
        }
    }

    private static MergeConflictFile CloneMergeConflictFile(MergeConflictFile source)
        => new()
        {
            FileName = source.FileName,
            DiffContent = source.DiffContent,
            Content = source.Content
        };

    private async Task CreatePullRequestAsync((string targetBranch, string? title) args)
    {
        if (IsGitOperationInProgress || Project == null || string.IsNullOrWhiteSpace(Project.WorkingPath) ||
            string.IsNullOrWhiteSpace(CurrentBranch) || string.IsNullOrWhiteSpace(args.targetBranch))
        {
            return;
        }

        IsGitOperationInProgress = true;
        _isCreatingPullRequest = true;
        _mergePreviewError = null;
        _mergePreviewMessage = null;
        GitOperationMessage = null;
        GitProgressMessage = $"Creating pull request from '{CurrentBranch}' to '{args.targetBranch}'...";
        StateHasChanged();

        try
        {
            var prTitle = !string.IsNullOrWhiteSpace(args.title)
                ? args.title
                : $"Merge {CurrentBranch} into {args.targetBranch}";

            var result = await VersionControlService.CreatePullRequestAsync(
                Project.WorkingPath,
                CurrentBranch,
                args.targetBranch,
                prTitle);

            if (!result.Success)
            {
                _mergePreviewError = result.Error ?? "Failed to create pull request.";
                return;
            }

            var message = !string.IsNullOrWhiteSpace(result.PullRequestUrl)
                ? $"Pull request created: {result.PullRequestUrl}"
                : result.Output ?? $"Pull request created from '{CurrentBranch}' to '{args.targetBranch}'.";

            CloseMergeBranchModal();

            NotificationService.ShowProjectSuccess(Project.Name, message);
        }
        catch (Exception ex)
        {
            _mergePreviewError = $"Error creating pull request: {ex.Message}";
        }
        finally
        {
            _isCreatingPullRequest = false;
            IsGitOperationInProgress = false;
            GitProgressMessage = null;
            StateHasChanged();
        }
    }

    private async Task OpenChangesTabAsync()
    {
        if (!IsGitRepository)
        {
            return;
        }

        _activeTab = "changes";
        await EnsureChangesJobsLoadedAsync();
        await RefreshUncommittedChangesStatus();
        await LoadChangesTabData();
    }

    private async Task ConfirmBranchSwitch((bool commitFirst, string? commitMessage) args)
    {
        if (string.IsNullOrEmpty(_targetBranch) || _targetBranch == CurrentBranch)
            return;

        if (Project == null || string.IsNullOrEmpty(Project.WorkingPath))
            return;

        _isSwitchingBranch = true;
        _branchSwitchError = null;
        StateHasChanged();

        try
        {
            if (args.commitFirst && _hasUncommittedChanges && !string.IsNullOrWhiteSpace(args.commitMessage))
            {
                var commitResult = await VersionControlService.CommitAllChangesAsync(
                Project.WorkingPath,
                args.commitMessage.Trim());

                if (!commitResult.Success)
                {
                    _branchSwitchError = $"Failed to commit changes: {commitResult.Error}";
                    _isSwitchingBranch = false;
                    StateHasChanged();
                    return;
                }
            }

            var result = await VersionControlService.HardCheckoutBranchAsync(
            Project.WorkingPath,
            _targetBranch,
            progressCallback: progress =>
            {
                GitProgressMessage = progress;
                InvokeAsync(StateHasChanged);
            });

            if (result.Success)
            {
                GitOperationMessage = result.Output ?? $"Successfully switched to branch '{_targetBranch}'";
                GitOperationSuccess = true;
                CurrentBranch = _targetBranch;
                CurrentCommitHash = result.CommitHash;
                await LoadBranches();
                await RefreshUncommittedChangesStatus();
                CloseBranchSwitchModal();
            }
            else
            {
                _branchSwitchError = result.Error ?? "Failed to switch branch";
            }
        }
        catch (Exception ex)
        {
            _branchSwitchError = $"Error switching branch: {ex.Message}";
        }
        finally
        {
            _isSwitchingBranch = false;
            GitProgressMessage = null;
            StateHasChanged();
        }
    }

    private async Task SyncWithOrigin()
    {
        if (IsGitOperationInProgress) return;
        if (Project == null || string.IsNullOrEmpty(Project.WorkingPath)) return;

        IsGitOperationInProgress = true;
        GitOperationMessage = null;
        GitProgressMessage = null;
        StateHasChanged();

        try
        {
            var result = await VersionControlService.SyncWithOriginAsync(Project.WorkingPath);

            if (result.Success)
            {
                CurrentCommitHash = result.CommitHash;
                await RefreshUncommittedChangesStatus();
                await PopulateGitHubRepositoryIfMissing();
                NotificationService.ShowProjectSuccess(
                    Project.Name,
                    string.IsNullOrWhiteSpace(CurrentBranch)
                        ? "Successfully synced with origin."
                        : $"Successfully synced with origin/{CurrentBranch}.");
            }
            else
            {
                NotificationService.ShowProjectError(Project.Name, result.Error ?? "Failed to sync with origin.");
            }
        }
        catch (Exception ex)
        {
            NotificationService.ShowProjectError(Project.Name, $"Error syncing with origin: {ex.Message}");
        }
        finally
        {
            IsGitOperationInProgress = false;
            GitProgressMessage = null;
            StateHasChanged();
        }
    }

    private async Task PopulateGitHubRepositoryIfMissing()
    {
        if (Project == null || string.IsNullOrEmpty(Project.WorkingPath)) return;
        if (!string.IsNullOrWhiteSpace(Project.GitHubRepository)) return;

        try
        {
            var remoteUrl = await VersionControlService.GetRemoteUrlAsync(Project.WorkingPath);
            if (string.IsNullOrWhiteSpace(remoteUrl)) return;

            var gitHubRepo = VersionControlService.ExtractGitHubRepository(remoteUrl);
            if (string.IsNullOrWhiteSpace(gitHubRepo)) return;

            Project.GitHubRepository = gitHubRepo;
            await ProjectService.UpdateAsync(Project);
        }
        catch { }
    }

    private void ClearGitMessage()
    {
        GitOperationMessage = null;
        StateHasChanged();
    }

    private void ShowCreateBranchModal()
    {
        _showCreateBranchModal = true;
        _createBranchError = null;
    }

    private async Task CreateBranch(string branchName)
    {
        if (string.IsNullOrWhiteSpace(branchName)) return;
        if (Project == null || string.IsNullOrEmpty(Project.WorkingPath)) return;

        _isCreatingBranch = true;
        _createBranchError = null;
        StateHasChanged();

        try
        {
            var result = await VersionControlService.CreateBranchAsync(
            Project.WorkingPath,
            branchName.Trim(),
            switchToBranch: true);

            if (result.Success)
            {
                GitOperationMessage = $"Successfully created and switched to branch '{branchName.Trim()}'";
                GitOperationSuccess = true;
                CurrentBranch = branchName.Trim();
                CurrentCommitHash = result.CommitHash;
                await LoadBranches();
                _showCreateBranchModal = false;
            }
            else
            {
                _createBranchError = result.Error ?? "Failed to create branch";
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

    // Uncommitted changes modal methods
    private async Task ShowUncommittedChangesModal()
    {
        _showUncommittedChangesModal = true;
        _uncommittedDiffError = null;
        _uncommittedDiffFiles = new List<DiffFile>();
        _pendingCommitJobs = new List<Job>();
        _isLoadingUncommittedDiff = true;
        StateHasChanged();

        if (Project == null || string.IsNullOrEmpty(Project.WorkingPath))
        {
            _uncommittedDiffError = "Project path not available";
            _isLoadingUncommittedDiff = false;
            StateHasChanged();
            return;
        }

        try
        {
            await EnsureChangesJobsLoadedAsync(force: true);
            await RefreshUncommittedChangesStatus();
            var diffOutput = await VersionControlService.GetWorkingDirectoryDiffAsync(Project.WorkingPath);
            if (!string.IsNullOrEmpty(diffOutput))
            {
                _uncommittedDiffFiles = ParseGitDiff(diffOutput);
            }
            else if (_workingTreeStatus.ChangedFilesCount > 0)
            {
                _uncommittedDiffFiles = BuildPlaceholderDiffFiles(_workingTreeStatus.ChangedFiles);
            }
            else
            {
                _uncommittedDiffError = "No uncommitted changes found";
            }

            _pendingCommitJobs = GetPendingCommitAttributionJobs();
        }
        catch (Exception ex)
        {
            _uncommittedDiffError = $"Failed to load diff: {ex.Message}";
        }
        finally
        {
            _isLoadingUncommittedDiff = false;
            StateHasChanged();
        }
    }

    private async Task CommitAndPushUncommittedChanges(string commitMessage)
    {
        if (string.IsNullOrWhiteSpace(commitMessage)) return;
        if (Project == null || string.IsNullOrEmpty(Project.WorkingPath)) return;

        _isCommittingChanges = true;
        _uncommittedCommitError = null;
        _pushRetryAvailable = false;
        _lastSuccessfulCommitHash = null;
        _commitPushStep = "Staging and committing...";
        StateHasChanged();

        try
        {
            var commitResult = await VersionControlService.CommitAllChangesAsync(
            Project.WorkingPath,
            commitMessage.Trim());

            if (!commitResult.Success)
            {
                _uncommittedCommitError = $"Failed to commit: {commitResult.Error}";
                _commitPushStep = null;
                return;
            }

            _lastSuccessfulCommitHash = commitResult.CommitHash;

            await LinkJobsToCommitAsync(_pendingCommitJobs, commitResult.CommitHash);

            _commitPushStep = "Pushing to remote...";
            StateHasChanged();

            var pushResult = await VersionControlService.PushAsync(Project.WorkingPath);

            if (pushResult.Success)
            {
                var jobCount = _pendingCommitJobs.Count;
                GitOperationMessage = jobCount > 0
                ? $"Successfully committed and pushed changes. {jobCount} job(s) linked to commit."
                : "Successfully committed and pushed changes";
                GitOperationSuccess = true;
                CurrentCommitHash = commitResult.CommitHash;
                _showUncommittedChangesModal = false;
                await SynchronizeProjectRepositoryStateAsync();
            }
            else
            {
                var shortHash = commitResult.CommitHash?[..Math.Min(7, commitResult.CommitHash?.Length ?? 0)] ?? "unknown";
                _uncommittedCommitError = $"Commit succeeded ({shortHash}) but push failed: {pushResult.Error}";
                _pushRetryAvailable = true;
                await SynchronizeProjectRepositoryStateAsync();
            }
        }
        catch (Exception ex)
        {
            _uncommittedCommitError = $"Error: {ex.Message}";
        }
        finally
        {
            _isCommittingChanges = false;
            _commitPushStep = null;
            StateHasChanged();
        }
    }

    // Commit Only handler (used by both UncommittedChangesModal and Changes tab)
    private async Task CommitOnlyUncommittedChanges(string commitMessage)
    {
        if (string.IsNullOrWhiteSpace(commitMessage)) return;
        if (Project == null || string.IsNullOrEmpty(Project.WorkingPath)) return;

        _isCommittingChanges = true;
        _uncommittedCommitError = null;
        _commitPushStep = "Staging and committing...";
        StateHasChanged();

        try
        {
            var commitResult = await VersionControlService.CommitAllChangesAsync(
                Project.WorkingPath,
                commitMessage.Trim());

            if (!commitResult.Success)
            {
                _uncommittedCommitError = $"Failed to commit: {commitResult.Error}";
                _commitPushStep = null;
                return;
            }

            await LinkJobsToCommitAsync(_pendingCommitJobs, commitResult.CommitHash);

            var jobCount = _pendingCommitJobs.Count;
            GitOperationMessage = jobCount > 0
                ? $"Successfully committed changes. {jobCount} job(s) linked to commit."
                : "Successfully committed changes";
            GitOperationSuccess = true;
            CurrentCommitHash = commitResult.CommitHash;
            _showUncommittedChangesModal = false;
            await SynchronizeProjectRepositoryStateAsync();
        }
        catch (Exception ex)
        {
            _uncommittedCommitError = $"Error: {ex.Message}";
        }
        finally
        {
            _isCommittingChanges = false;
            _commitPushStep = null;
            StateHasChanged();
        }
    }

    private void ShowDiscardConfirmation()
    {
        _showDiscardConfirmation = true;
    }

    private void CloseDiscardConfirmation()
    {
        _showDiscardConfirmation = false;
    }

    private async Task DiscardAllChanges()
    {
        if (Project == null || string.IsNullOrEmpty(Project.WorkingPath)) return;

        _isDiscardingChanges = true;
        StateHasChanged();

        try
        {
            var result = await VersionControlService.DiscardAllChangesAsync(Project.WorkingPath);

            if (result.Success)
            {
                await ClearPendingChangeAttributionAsync(GetPendingCommitAttributionJobs());
                GitOperationMessage = "Successfully discarded all uncommitted changes";
                GitOperationSuccess = true;
                _showDiscardConfirmation = false;
                _showUncommittedChangesModal = false;
                await SynchronizeProjectRepositoryStateAsync();
            }
            else
            {
                GitOperationMessage = $"Failed to discard changes: {result.Error}";
                GitOperationSuccess = false;
                _showDiscardConfirmation = false;
            }
        }
        catch (Exception ex)
        {
            GitOperationMessage = $"Error discarding changes: {ex.Message}";
            GitOperationSuccess = false;
            _showDiscardConfirmation = false;
        }
        finally
        {
            _isDiscardingChanges = false;
            StateHasChanged();
        }
    }

    private async Task RetryPush()
    {
        if (Project == null || string.IsNullOrEmpty(Project.WorkingPath)) return;

        _isCommittingChanges = true;
        _uncommittedCommitError = null;
        _pushRetryAvailable = false;
        _commitPushStep = "Pushing to remote...";
        StateHasChanged();

        try
        {
            var pushResult = await VersionControlService.PushAsync(Project.WorkingPath);

            if (pushResult.Success)
            {
                GitOperationMessage = "Successfully pushed changes";
                GitOperationSuccess = true;
                CurrentCommitHash = _lastSuccessfulCommitHash;
                _showUncommittedChangesModal = false;
                await RefreshUncommittedChangesStatus();
            }
            else
            {
                _uncommittedCommitError = $"Push failed: {pushResult.Error}";
                _pushRetryAvailable = true;
            }
        }
        catch (Exception ex)
        {
            _uncommittedCommitError = $"Error: {ex.Message}";
            _pushRetryAvailable = true;
        }
        finally
        {
            _isCommittingChanges = false;
            _commitPushStep = null;
            StateHasChanged();
        }
    }

    private async Task PruneRemoteBranches()
    {
        if (IsGitOperationInProgress) return;
        if (Project == null || string.IsNullOrEmpty(Project.WorkingPath)) return;

        IsGitOperationInProgress = true;
        GitOperationMessage = null;
        GitProgressMessage = "Pruning stale branches...";
        StateHasChanged();

        try
        {
            var result = await VersionControlService.PruneRemoteBranchesAsync(Project.WorkingPath);

            if (result.Success)
            {
                GitOperationMessage = result.Output ?? "Prune completed successfully";
                GitOperationSuccess = true;
                await LoadBranches();
            }
            else
            {
                GitOperationMessage = result.Error ?? "Failed to prune branches";
                GitOperationSuccess = false;
            }
        }
        catch (Exception ex)
        {
            GitOperationMessage = $"Error pruning branches: {ex.Message}";
            GitOperationSuccess = false;
        }
        finally
        {
            IsGitOperationInProgress = false;
            GitProgressMessage = null;
            StateHasChanged();
        }
    }

    // Changes tab methods (delegated to ProjectChangesTab component)
    private async Task LoadChangesTabData()
    {
        if (_changesTab != null)
        {
            await _changesTab.LoadDataAsync();
        }
    }

    private async Task HandleChangesTabGitOperation(string? message)
    {
        if (!string.IsNullOrEmpty(message))
        {
            GitOperationMessage = message;
            GitOperationSuccess = true;
        }
        await SynchronizeProjectRepositoryStateAsync();
    }

    private async Task HandleChangesTabCommitted()
    {
        await RefreshJobs();
        _changesJobs = null; // Invalidate cached changes jobs
        await RefreshUncommittedChangesStatus();
        if (Project != null && !string.IsNullOrEmpty(Project.WorkingPath) && IsGitRepository)
        {
            CurrentCommitHash = await VersionControlService.GetCurrentCommitHashAsync(Project.WorkingPath);
        }
        _activeTab = "jobs";
        StateHasChanged();
    }



    private static string ShortCommitHash(string? commitHash)
    {
        if (string.IsNullOrWhiteSpace(commitHash))
        {
            return "unknown commit";
        }

        return commitHash[..Math.Min(7, commitHash.Length)];
    }

    private static List<DiffFile> BuildPlaceholderDiffFiles(IReadOnlyList<string> changedFiles)
    {
        return changedFiles
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => new DiffFile
            {
                FileName = path,
                DiffContent = "Diff content is unavailable, but Git reported this file as changed.",
                Additions = 0,
                Deletions = 0
            })
            .ToList();
    }

    private static List<DiffFile> ParseGitDiff(string diffOutput)
    {
        return GitDiffParser.ParseDiff(diffOutput);
    }

    private static string FormatDiffContent(string diffContent)
    {
        if (string.IsNullOrWhiteSpace(diffContent))
            return "<span class=\"text-muted\">No changes</span>";

        return GitDiffParser.FormatDiffHtml(diffContent);
    }

    private List<Job> GetPendingCommitAttributionJobs()
    {
        return (_changesJobs ?? [])
            .Where(job => job.Status == JobStatus.Completed && string.IsNullOrWhiteSpace(job.GitCommitHash))
            .OrderByDescending(job => job.CompletedAt ?? job.StartedAt ?? job.CreatedAt)
            .ToList();
    }

    private async Task LinkJobsToCommitAsync(IEnumerable<Job> jobs, string? commitHash)
    {
        if (string.IsNullOrWhiteSpace(commitHash))
        {
            return;
        }

        foreach (var job in jobs
            .Where(job => job.Id != Guid.Empty)
            .DistinctBy(job => job.Id))
        {
            if (!await JobService.UpdateGitCommitHashAsync(job.Id, commitHash))
            {
                continue;
            }

            var localJob = _changesJobs?.FirstOrDefault(existingJob => existingJob.Id == job.Id);
            if (localJob != null)
            {
                localJob.GitCommitHash = commitHash;
            }
        }
    }

    private async Task ClearPendingChangeAttributionAsync(IEnumerable<Job> jobs)
    {
        foreach (var job in jobs
            .Where(job => job.Status == JobStatus.Completed && string.IsNullOrWhiteSpace(job.GitCommitHash))
            .DistinctBy(job => job.Id))
        {
            if (!await JobService.UpdateGitDiffAsync(job.Id, null))
            {
                continue;
            }

            var localJob = _changesJobs?.FirstOrDefault(existingJob => existingJob.Id == job.Id);
            if (localJob != null)
            {
                localJob.GitDiff = null;
                localJob.ChangedFilesCount = 0;
            }
        }
    }

    private async Task SynchronizeProjectRepositoryStateAsync(bool reloadChangesTab = false)
    {
        await RefreshJobs();
        _changesJobs = null; // Invalidate cached changes jobs
        await RefreshUncommittedChangesStatus();

        if (Project != null && !string.IsNullOrEmpty(Project.WorkingPath) && IsGitRepository)
        {
            CurrentCommitHash = await VersionControlService.GetCurrentCommitHashAsync(Project.WorkingPath);
        }

        if (reloadChangesTab || _activeTab == "changes")
        {
            await EnsureChangesJobsLoadedAsync(force: true);
            await LoadChangesTabData();
        }
    }
}
