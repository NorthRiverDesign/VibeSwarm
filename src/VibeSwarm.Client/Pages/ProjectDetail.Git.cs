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
        _mergePreviewError = null;
        _mergePreviewMessage = null;
        _mergeTargetBranch = SelectDefaultMergeTargetBranch();
        _showMergeBranchModal = true;
        StateHasChanged();

        await PreviewMergeBranchAsync();
    }

    private void CloseMergeBranchModal()
    {
        _showMergeBranchModal = false;
        _mergeTargetBranch = null;
        _mergePushAfterMerge = true;
        _mergePreviewMessage = null;
        _mergePreviewError = null;
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
        StateHasChanged();

        try
        {
            var result = await VersionControlService.PreviewMergeBranchAsync(
                Project.WorkingPath,
                CurrentBranch,
                _mergeTargetBranch);

            if (result.Success)
            {
                _mergePreviewMessage = result.Output ?? $"'{CurrentBranch}' can be merged into '{_mergeTargetBranch}' without conflicts.";
            }
            else
            {
                _mergePreviewError = result.Error ?? $"{AppConstants.AppName} could not verify the merge preview.";
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
            var previewResult = await VersionControlService.PreviewMergeBranchAsync(
                Project.WorkingPath,
                CurrentBranch,
                args.targetBranch);

            if (!previewResult.Success)
            {
                _mergePreviewError = previewResult.Error ?? $"{AppConstants.AppName} could not verify the merge preview.";
                return;
            }

            _mergePreviewMessage = previewResult.Output ?? $"'{CurrentBranch}' can be merged into '{args.targetBranch}' without conflicts.";

            var result = await VersionControlService.MergeBranchAsync(
                Project.WorkingPath,
                CurrentBranch,
                args.targetBranch,
                progressCallback: progress =>
                {
                    GitProgressMessage = progress;
                    InvokeAsync(StateHasChanged);
                },
                pushAfterMerge: args.pushAfterMerge);

            if (!result.Success)
            {
                _mergePreviewError = result.Error ?? "Failed to merge branches.";
                return;
            }

            GitOperationMessage = result.Output ?? $"Merged '{CurrentBranch}' into '{args.targetBranch}'.";
            GitOperationSuccess = true;
            await LoadBranches();
            CloseMergeBranchModal();

            NotificationService.ShowSuccess(result.Output ?? $"Merged '{CurrentBranch}' into '{args.targetBranch}'.");
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

    private async Task OpenChangesTabAsync()
    {
        if (!IsGitRepository)
        {
            return;
        }

        _activeTab = "changes";
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
        StateHasChanged();

        try
        {
            var result = await VersionControlService.SyncWithOriginAsync(
            Project.WorkingPath,
            progressCallback: progress =>
            {
                GitProgressMessage = progress;
                InvokeAsync(StateHasChanged);
            });

            if (result.Success)
            {
                GitOperationMessage = result.Output ?? $"Successfully synced with origin/{CurrentBranch}";
                GitOperationSuccess = true;
                CurrentCommitHash = result.CommitHash;
                await RefreshUncommittedChangesStatus();
                await PopulateGitHubRepositoryIfMissing();
            }
            else
            {
                GitOperationMessage = result.Error ?? "Failed to sync with origin";
                GitOperationSuccess = false;
            }
        }
        catch (Exception ex)
        {
            GitOperationMessage = $"Error syncing with origin: {ex.Message}";
            GitOperationSuccess = false;
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

            // Load completed jobs without a commit hash (pending attribution)
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

    // Changes tab methods
    private async Task LoadChangesTabData()
    {
        if (_isLoadingChangesTab) return;
        if (Project == null || string.IsNullOrEmpty(Project.WorkingPath)) return;

        var previousDiffFiles = _changesTabDiffFiles;
        var previousExpandedItems = _changesTabExpandedItems;
        var shouldShowLoadingState = !_changesTabDiffFiles.Any() && string.IsNullOrEmpty(_changesTabLoadError);

        _isLoadingChangesTab = true;
        _changesTabCommitError = null;
        _changesTabLoadError = null;
        if (shouldShowLoadingState)
        {
            StateHasChanged();
        }

        try
        {
            var nextDiffFiles = new List<DiffFile>();
            var nextPendingJobs = new List<Job>();
            var nextCommittedJobs = new List<Job>();
            var nextShowingCommittedDiff = false;
            var nextShowingPersistedDiff = false;
            string? nextCommittedHash = null;
            string? nextEmptyStateMessage = null;

            await RefreshUncommittedChangesStatus();
            var changeSelection = GetProjectChangesSelection();
            string? diffOutput = null;
            IReadOnlyList<string> changedFiles = [];

            if (changeSelection.SourceType == ProjectChangesSourceType.WorkingDirectory)
            {
                changedFiles = _workingTreeStatus.ChangedFiles;
                if (changedFiles.Count == 0 && Math.Max(_workingTreeStatus.ChangedFilesCount, _uncommittedFilesCount) > 0)
                {
                    changedFiles = await VersionControlService.GetChangedFilesAsync(Project.WorkingPath);
                }

                diffOutput = await VersionControlService.GetWorkingDirectoryDiffAsync(Project.WorkingPath);
                nextPendingJobs = changeSelection.LinkedJobs.ToList();
                nextEmptyStateMessage = "No uncommitted changes. Your working directory is clean.";
            }
            else if (changeSelection.SourceType == ProjectChangesSourceType.Commit)
            {
                nextShowingCommittedDiff = true;
                nextCommittedHash = changeSelection.CommitHash;
                nextCommittedJobs = changeSelection.LinkedJobs.ToList();
                diffOutput = await LoadCommittedChangesDiffAsync(changeSelection);
                if (string.IsNullOrWhiteSpace(diffOutput))
                {
                    diffOutput = GetPersistedDiffFallback(changeSelection);
                }
                nextEmptyStateMessage = $"No diff is available for commit {ShortCommitHash(changeSelection.CommitHash)}.";
            }
            else if (changeSelection.SourceType == ProjectChangesSourceType.PersistedJobDiff)
            {
                nextShowingPersistedDiff = true;
                nextPendingJobs = changeSelection.LinkedJobs.ToList();
                diffOutput = changeSelection.PersistedDiff;
                nextEmptyStateMessage = "No captured diff is available on the latest job.";
            }
            else
            {
                nextEmptyStateMessage = "No project changes are available to show.";
            }

            if (!string.IsNullOrEmpty(diffOutput))
            {
                nextDiffFiles = ParseGitDiff(diffOutput);
            }

            if (nextDiffFiles.Count == 0 && changedFiles.Count > 0)
            {
                nextDiffFiles = BuildPlaceholderDiffFiles(changedFiles);
            }

            _changesTabDiffFiles = nextDiffFiles;
            _changesTabPendingJobs = nextPendingJobs;
            _changesTabCommittedJobs = nextCommittedJobs;
            _changesTabShowingCommittedDiff = nextShowingCommittedDiff;
            _changesTabShowingPersistedDiff = nextShowingPersistedDiff;
            _changesTabCommittedHash = nextCommittedHash;
            _changesTabEmptyStateMessage = nextEmptyStateMessage;
            _changesTabExpandedItems = ProjectChangesTabState.PreserveExpandedItems(
                previousDiffFiles,
                previousExpandedItems,
                nextDiffFiles);
        }
        catch (Exception ex)
        {
            _changesTabLoadError = $"Failed to load changes: {ex.Message}";
        }
        finally
        {
            _isLoadingChangesTab = false;
            StateHasChanged();
        }
    }

    private ProjectChangesSelection GetProjectChangesSelection()
        => ProjectChangesSectionResolver.Resolve(
            Jobs,
            _workingTreeStatus.HasUncommittedChanges || _hasUncommittedChangesHeader,
            Math.Max(_workingTreeStatus.ChangedFilesCount, _uncommittedFilesCount));

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

    private static string? GetPersistedDiffFallback(ProjectChangesSelection changeSelection)
    {
        return changeSelection.LinkedJobs
            .Select(job => job.GitDiff)
            .FirstOrDefault(diff => !string.IsNullOrWhiteSpace(diff));
    }

    private async Task<string?> LoadCommittedChangesDiffAsync(ProjectChangesSelection changeSelection)
    {
        if (Project == null || string.IsNullOrEmpty(Project.WorkingPath) || string.IsNullOrWhiteSpace(changeSelection.CommitHash))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(changeSelection.BaseCommit) &&
            !string.Equals(changeSelection.BaseCommit, changeSelection.CommitHash, StringComparison.OrdinalIgnoreCase))
        {
            var rangeDiff = await VersionControlService.GetCommitRangeDiffAsync(
                Project.WorkingPath,
                changeSelection.BaseCommit,
                changeSelection.CommitHash);

            if (!string.IsNullOrWhiteSpace(rangeDiff))
            {
                return rangeDiff;
            }
        }

        return await VersionControlService.GetCommitRangeDiffAsync(
            Project.WorkingPath,
            $"{changeSelection.CommitHash}^",
            changeSelection.CommitHash);
    }

    private static string ShortCommitHash(string? commitHash)
    {
        if (string.IsNullOrWhiteSpace(commitHash))
        {
            return "unknown commit";
        }

        return commitHash[..Math.Min(7, commitHash.Length)];
    }

    private void ToggleChangesTabExpanded(int index)
    {
        if (!_changesTabExpandedItems.Add(index))
        {
            _changesTabExpandedItems.Remove(index);
        }
    }

    private void ExpandAllChangesTab()
    {
        _changesTabExpandedItems = Enumerable.Range(0, _changesTabDiffFiles.Count).ToHashSet();
    }

    private void CollapseAllChangesTab()
    {
        _changesTabExpandedItems.Clear();
    }

    private void GenerateChangesTabCommitMessage()
    {
        if (_changesTabPendingJobs == null || !_changesTabPendingJobs.Any()) return;

        var sb = new System.Text.StringBuilder();
        if (_changesTabPendingJobs.Count == 1)
        {
            var job = _changesTabPendingJobs[0];
            sb.AppendLine(job.Title ?? "Feature implementation");
            sb.AppendLine();
            sb.AppendLine($"Job #{job.Id}: {TruncateForToast(job.GoalPrompt, 60)}");
        }
        else
        {
            sb.AppendLine($"Implement {_changesTabPendingJobs.Count} features");
            sb.AppendLine();
            foreach (var job in _changesTabPendingJobs.Take(10))
            {
                sb.AppendLine($"- Job #{job.Id}: {TruncateForToast(job.Title ?? job.GoalPrompt, 50)}");
            }
            if (_changesTabPendingJobs.Count > 10)
            {
                sb.AppendLine($"- ... and {_changesTabPendingJobs.Count - 10} more");
            }
        }
        _changesTabCommitMessage = sb.ToString().TrimEnd();
    }

    private async Task ChangesTabCommitOnly()
    {
        if (string.IsNullOrWhiteSpace(_changesTabCommitMessage)) return;
        if (Project == null || string.IsNullOrEmpty(Project.WorkingPath)) return;

        _isChangesTabCommitting = true;
        _isChangesTabCommitOnly = true;
        _changesTabCommitError = null;
        StateHasChanged();

        try
        {
            var commitResult = await VersionControlService.CommitAllChangesAsync(
                Project.WorkingPath,
                _changesTabCommitMessage.Trim());

            if (!commitResult.Success)
            {
                _changesTabCommitError = $"Failed to commit: {commitResult.Error}";
                return;
            }

            await LinkJobsToCommitAsync(_changesTabPendingJobs, commitResult.CommitHash);

            GitOperationMessage = "Successfully committed changes";
            GitOperationSuccess = true;
            CurrentCommitHash = commitResult.CommitHash;
            _changesTabCommitMessage = string.Empty;
            await SynchronizeProjectRepositoryStateAsync();
            _activeTab = "jobs"; // Switch away from empty changes tab
        }
        catch (Exception ex)
        {
            _changesTabCommitError = $"Error: {ex.Message}";
        }
        finally
        {
            _isChangesTabCommitting = false;
            _isChangesTabCommitOnly = false;
            StateHasChanged();
        }
    }

    private async Task ChangesTabCommitAndPush()
    {
        if (string.IsNullOrWhiteSpace(_changesTabCommitMessage)) return;
        if (Project == null || string.IsNullOrEmpty(Project.WorkingPath)) return;

        _isChangesTabCommitting = true;
        _isChangesTabCommitOnly = false;
        _changesTabCommitError = null;
        StateHasChanged();

        try
        {
            var commitResult = await VersionControlService.CommitAllChangesAsync(
                Project.WorkingPath,
                _changesTabCommitMessage.Trim());

            if (!commitResult.Success)
            {
                _changesTabCommitError = $"Failed to commit: {commitResult.Error}";
                return;
            }

            await LinkJobsToCommitAsync(_changesTabPendingJobs, commitResult.CommitHash);

            var pushResult = await VersionControlService.PushAsync(Project.WorkingPath);
            if (pushResult.Success)
            {
                GitOperationMessage = "Successfully committed and pushed changes";
                GitOperationSuccess = true;
                CurrentCommitHash = commitResult.CommitHash;
                _changesTabCommitMessage = string.Empty;
                await SynchronizeProjectRepositoryStateAsync(reloadChangesTab: true);
                _activeTab = "jobs";
            }
            else
            {
                _changesTabCommitError = $"Commit succeeded but push failed: {pushResult.Error}";
                _changesTabCommitMessage = string.Empty;
                await SynchronizeProjectRepositoryStateAsync(reloadChangesTab: true);
            }
        }
        catch (Exception ex)
        {
            _changesTabCommitError = $"Error: {ex.Message}";
        }
        finally
        {
            _isChangesTabCommitting = false;
            StateHasChanged();
        }
    }

    private async Task ChangesTabDiscardAll()
    {
        if (Project == null || string.IsNullOrEmpty(Project.WorkingPath)) return;

        _isChangesTabDiscarding = true;
        StateHasChanged();

        try
        {
            var result = await VersionControlService.DiscardAllChangesAsync(Project.WorkingPath);
            if (result.Success)
            {
                await ClearPendingChangeAttributionAsync(_changesTabPendingJobs);
                GitOperationMessage = "Successfully discarded all uncommitted changes";
                GitOperationSuccess = true;
                _changesTabShowDiscardConfirm = false;
                await SynchronizeProjectRepositoryStateAsync();
                _activeTab = "jobs";
            }
            else
            {
                _changesTabCommitError = $"Failed to discard: {result.Error}";
                _changesTabShowDiscardConfirm = false;
            }
        }
        catch (Exception ex)
        {
            _changesTabCommitError = $"Error: {ex.Message}";
            _changesTabShowDiscardConfirm = false;
        }
        finally
        {
            _isChangesTabDiscarding = false;
            StateHasChanged();
        }
    }

    private static string FormatDiffContent(string diffContent)
    {
        if (string.IsNullOrWhiteSpace(diffContent))
            return "<span class=\"text-muted\">No changes</span>";

        return GitDiffParser.FormatDiffHtml(diffContent);
    }

    private List<DiffFile> ParseGitDiff(string diffOutput)
    {
        return GitDiffParser.ParseDiff(diffOutput);
    }

    private List<Job> GetPendingCommitAttributionJobs()
    {
        return Jobs
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

            var localJob = Jobs.FirstOrDefault(existingJob => existingJob.Id == job.Id);
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

            var localJob = Jobs.FirstOrDefault(existingJob => existingJob.Id == job.Id);
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
        await RefreshUncommittedChangesStatus();

        if (Project != null && !string.IsNullOrEmpty(Project.WorkingPath) && IsGitRepository)
        {
            CurrentCommitHash = await VersionControlService.GetCurrentCommitHashAsync(Project.WorkingPath);
        }

        if (reloadChangesTab || _activeTab == "changes")
        {
            await LoadChangesTabData();
        }
    }
}
