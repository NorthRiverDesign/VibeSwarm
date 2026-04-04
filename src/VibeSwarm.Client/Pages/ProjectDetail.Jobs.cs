using VibeSwarm.Client.Components.Projects;
using VibeSwarm.Client.Models;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Client.Pages;

public partial class ProjectDetail
{
    private string _jobSearchQuery = string.Empty;
    private string _jobStatusFilter = "all";

    private static readonly TimeSpan JobsRefreshDebounceInterval = TimeSpan.FromSeconds(2);
    private DateTime _lastJobsRefreshTime = DateTime.MinValue;
    private bool _isRefreshingJobs;

    private async Task RefreshJobs(bool force = false)
    {
        if (Project == null) return;

        if (!force && _isRefreshingJobs) return;

        if (!force && (DateTime.UtcNow - _lastJobsRefreshTime) < JobsRefreshDebounceInterval)
            return;

        _isRefreshingJobs = true;
        try
        {
            var allJobsTask = JobService.GetByProjectIdAsync(ProjectId);
            var pagedJobsTask = JobService.GetPagedByProjectIdAsync(ProjectId, _jobsPageNumber, ProjectJobsPageSize, _jobSearchQuery, _jobStatusFilter);
            await Task.WhenAll(allJobsTask, pagedJobsTask);

            Jobs = allJobsTask.Result.ToList();
            ProjectActiveJobsCount = Jobs.Count(job =>
                job.Status == JobStatus.New ||
                job.Status == JobStatus.Pending ||
                job.Status == JobStatus.Started ||
                job.Status == JobStatus.Planning ||
                job.Status == JobStatus.Processing ||
                job.Status == JobStatus.Paused ||
                job.Status == JobStatus.Stalled);

            var result = pagedJobsTask.Result;
            _jobsPageNumber = result.PageNumber;
            PagedJobs = result.Items;
            JobsTotalCount = result.TotalCount;
            ProjectCompletedJobsCount = result.CompletedCount;
            _lastJobsRefreshTime = DateTime.UtcNow;
        }
        catch (Exception)
        {
            // Swallow transient API errors during background refresh to prevent
            // unhandled exceptions from crashing the Blazor WASM runtime
        }
        finally
        {
            _isRefreshingJobs = false;
        }
    }

    private async Task HandleJobSearchChanged(string search)
    {
        _jobSearchQuery = search;
        _jobsPageNumber = 1;
        await RefreshProjectJobsPage();
    }

    private async Task HandleJobStatusFilterChanged(string statusFilter)
    {
        _jobStatusFilter = statusFilter;
        _jobsPageNumber = 1;
        await RefreshProjectJobsPage();
    }

    private async Task ShowCreateJobModal()
    {
        await LoadJobTemplates();

        // Reset the form for a new job
        NewJob = new Job();
        NewJob.Branch = CurrentBranch;
        NewJob.GitChangeDeliveryMode = Project?.GitChangeDeliveryMode ?? GitChangeDeliveryMode.CommitToBranch;
        NewJob.TargetBranch = Project?.DefaultTargetBranch ?? CurrentBranch;
        SelectedModelId = string.Empty;
        AvailableModels.Clear();
        NewJob.ReasoningEffort = null;

        var initialProvider = GetPreferredJobProvider();
        if (initialProvider != null)
        {
            NewJob.ProviderId = initialProvider.Id;
            await LoadModelsForProvider(initialProvider.Id);
        }

        ErrorMessage = null;
        _showCreateJobModal = true;
    }

    private async Task LoadJobTemplates()
    {
        JobTemplates = (await JobTemplateService.GetAllAsync()).ToList();
    }

    private async Task HandleCreateJob()
    {
        if (NewJob.ProviderId == Guid.Empty)
        {
            ErrorMessage = "Please select a provider.";
            return;
        }

        if (!GetAllowedJobProviders().Any(p => p.Id == NewJob.ProviderId))
        {
            ErrorMessage = "The selected provider is not allowed for this project.";
            return;
        }

        if (NewJob.GitChangeDeliveryMode == GitChangeDeliveryMode.PullRequest)
        {
            if (string.IsNullOrWhiteSpace(NewJob.TargetBranch))
            {
                ErrorMessage = "Please choose a target branch for the pull request.";
                return;
            }

            if (string.IsNullOrWhiteSpace(NewJob.Branch))
            {
                ErrorMessage = "Please choose a source branch for the pull request.";
                return;
            }

            if (string.Equals(NewJob.Branch, NewJob.TargetBranch, StringComparison.Ordinal))
            {
                ErrorMessage = "Pull requests need a source branch that differs from the target branch.";
                return;
            }
        }

        IsSaving = true;
        ErrorMessage = null;

        try
        {
            NewJob.ProjectId = ProjectId;
            NewJob.ModelUsed = string.IsNullOrEmpty(SelectedModelId) ? null : SelectedModelId;
            var createdJob = await JobService.CreateAsync(NewJob);
            NavigationManager.NavigateTo($"/jobs/view/{createdJob.Id}");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error creating job: {ex.Message}";
            IsSaving = false;
        }
    }

    private async Task OnProviderChanged(Guid providerId)
    {
        if (providerId != Guid.Empty && !GetAllowedJobProviders().Any(p => p.Id == providerId))
        {
            ErrorMessage = "This provider is not enabled for the current project.";
            return;
        }

        NewJob.ProviderId = providerId;
        SelectedModelId = string.Empty;
        AvailableModels.Clear();
        ErrorMessage = null;

        if (providerId != Guid.Empty)
        {
            await LoadModelsForProvider(providerId);
        }
    }

    private async Task LoadModelsForProvider(Guid providerId)
    {
        IsLoadingModels = true;
        try
        {
            AvailableModels = (await ProviderService.GetModelsAsync(providerId))
                .Where(model => model.IsAvailable)
                .OrderByDescending(model => model.IsDefault)
                .ThenBy(model => model.DisplayName ?? model.ModelId)
                .ToList();

            SelectedModelId = ProjectExecutionDefaults.ResolveModelId(Project, providerId, AvailableModels);
            var provider = GetAllowedJobProviders().FirstOrDefault(item => item.Id == providerId);
            NewJob.ReasoningEffort = provider != null
                ? ProjectExecutionDefaults.ResolveReasoningEffort(Project, provider)
                : null;
        }
        finally
        {
            IsLoadingModels = false;
        }
    }

    private List<Provider> GetAllowedJobProviders()
    {
        return ProjectExecutionDefaults.GetAllowedProviders(Project, Providers);
    }

    private Provider? GetPreferredJobProvider()
    {
        return ProjectExecutionDefaults.GetPreferredProvider(Project, Providers);
    }

    private Guid? GetDefaultIdeasProcessingProviderId()
    {
        return GetPreferredJobProvider()?.Id;
    }

    private string? GetDefaultIdeasProcessingModelId()
    {
        var provider = GetPreferredJobProvider();
        if (provider == null)
        {
            return null;
        }

        var models = provider.AvailableModels
            .Where(model => model.IsAvailable)
            .OrderByDescending(model => model.IsDefault)
            .ThenBy(model => model.DisplayName ?? model.ModelId)
            .ToList();

        var resolvedModelId = ProjectExecutionDefaults.ResolveModelId(Project, provider.Id, models);
        return string.IsNullOrWhiteSpace(resolvedModelId) ? null : resolvedModelId;
    }

    private string? GetProviderHint()
    {
        var allowedProviders = GetAllowedJobProviders();
        if (allowedProviders.Count == 0)
        {
            return null;
        }

        var hasProjectPriority = Project?.ProviderSelections?.Any(pp => pp.IsEnabled) == true;
        if (!hasProjectPriority)
        {
            return "This project can use any enabled provider. The list below is ordered by the current provider defaults.";
        }

        var chain = string.Join(" -> ", allowedProviders.Select(p => p.Name));
        return $"This project will try providers in this order: {chain}.";
    }

    private async Task DeleteJob(Guid jobId)
    {
        try
        {
            await JobService.DeleteAsync(jobId);
            ClampJobsPageNumber(Math.Max(JobsTotalCount - 1, 0));
            await RefreshJobs();
            NotificationService.ShowSuccess("Job deleted successfully.");
        }
        catch (Exception ex)
        {
            NotificationService.ShowError($"Error deleting job: {ex.Message}");
        }
    }

    private async Task RetryJob(Guid jobId)
    {
        try
        {
            var success = await JobService.ResetJobWithOptionsAsync(jobId);
            if (success)
            {
                await RefreshJobs();
                NotificationService.ShowSuccess("Job queued for retry.");
            }
            else
            {
                NotificationService.ShowError("Could not retry this job. It may no longer be in a retryable state.");
            }
        }
        catch (Exception ex)
        {
            NotificationService.ShowError($"Error retrying job: {ex.Message}");
        }
    }

    private async Task RetrySelectedJobs(List<Guid> jobIds)
    {
        try
        {
            var count = await JobService.RetrySelectedByProjectIdAsync(ProjectId, jobIds);
            if (count > 0)
            {
                await RefreshJobs();
                NotificationService.ShowSuccess($"Queued {count} selected job(s) for retry.");
            }
			else
			{
				NotificationService.ShowInfo("No selected failed, cancelled, or stopped jobs to retry.");
			}
        }
        catch (Exception ex)
        {
            NotificationService.ShowError($"Error retrying selected jobs: {ex.Message}");
        }
    }

    private async Task DeleteCompletedJobs()
    {
        try
        {
            var count = await JobService.DeleteCompletedByProjectIdAsync(ProjectId);
            if (count > 0)
            {
                ClampJobsPageNumber(Math.Max(JobsTotalCount - count, 0));
                await RefreshJobs();
                NotificationService.ShowSuccess($"Deleted {count} completed job(s).");
            }
            else
            {
                NotificationService.ShowInfo("No completed jobs to delete.");
            }
        }
        catch (Exception ex)
        {
            NotificationService.ShowError($"Error deleting completed jobs: {ex.Message}");
        }
    }

    private async Task CancelAllJobs()
    {
        try
        {
            var count = await JobService.CancelAllByProjectIdAsync(ProjectId);
            if (count > 0)
            {
                await RefreshJobs();
                NotificationService.ShowSuccess($"Cancelled {count} job(s).");
            }
            else
            {
                NotificationService.ShowInfo("No active jobs to cancel.");
            }
        }
        catch (Exception ex)
        {
            NotificationService.ShowError($"Error cancelling jobs: {ex.Message}");
        }
    }

    private async Task CancelSelectedJobs(List<Guid> jobIds)
    {
        try
        {
            var count = await JobService.CancelSelectedByProjectIdAsync(ProjectId, jobIds);
            if (count > 0)
            {
                await RefreshJobs();
                NotificationService.ShowSuccess($"Cancelled {count} selected job(s).");
            }
            else
            {
                NotificationService.ShowInfo("No selected active jobs to cancel.");
            }
        }
        catch (Exception ex)
        {
            NotificationService.ShowError($"Error cancelling selected jobs: {ex.Message}");
        }
    }

    private async Task PrioritizeSelectedJobs(List<Guid> jobIds)
    {
        try
        {
            var count = await JobService.PrioritizeSelectedByProjectIdAsync(ProjectId, jobIds);
            if (count > 0)
            {
                await RefreshJobs();
                NotificationService.ShowSuccess($"Prioritized {count} selected queued job(s).");
            }
            else
            {
                NotificationService.ShowInfo("No selected queued jobs to prioritize.");
            }
        }
        catch (Exception ex)
        {
            NotificationService.ShowError($"Error prioritizing selected jobs: {ex.Message}");
        }
    }

    // Ideas management methods
    private static readonly TimeSpan IdeasLoadDebounceInterval = TimeSpan.FromSeconds(2);

    private async Task<bool> LoadIdeas(bool force = false)
    {
        if (!force && (DateTime.UtcNow - _lastIdeasLoadTime) < IdeasLoadDebounceInterval)
        {
            return false;
        }

        try
        {
            var result = await IdeaService.GetPagedByProjectIdAsync(ProjectId, _ideasPageNumber, IdeasPageSize);
            _ideasPageNumber = result.PageNumber;
            Ideas = result.Items;
            IdeasTotalCount = result.TotalCount;
            IdeasUnprocessedCount = result.UnprocessedCount;
            var ideaIdsWithJobs = Ideas
                .Where(i => i.JobId.HasValue)
                .Select(i => i.Id)
                .ToHashSet();
            ProcessingIdeaIds.RemoveWhere(id => ideaIdsWithJobs.Contains(id) || Ideas.All(i => i.Id != id));
            IsIdeasProcessingActive = await IdeaService.IsProcessingActiveAsync(ProjectId);
            _lastIdeasLoadTime = DateTime.UtcNow;
            return true;
        }
        catch (Exception)
        {
            Ideas = new List<Idea>();
            IdeasTotalCount = 0;
            IdeasUnprocessedCount = 0;
            return true;
        }
    }

    private async Task AddIdea(CreateIdeaRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Description)) return;

        _isAddingIdea = true;
        StateHasChanged();

        try
        {
            request.ProjectId = ProjectId;
            request.Description = request.Description.Trim();

            var created = await IdeaService.CreateAsync(request);
            if (created != null)
            {
                _localIdeaCreateIds.Add(created.Id);
            }
            _ideasPageNumber = Math.Max(1, ((IdeasTotalCount + 1) + IdeasPageSize - 1) / IdeasPageSize);
            await LoadIdeas(force: true);
        }
        catch (Exception ex)
        {
            NotificationService.ShowError($"Error adding idea: {ex.Message}");
        }
        finally
        {
            _isAddingIdea = false;
            StateHasChanged();
        }
    }

    private async Task UpdateIdea((Guid IdeaId, string Description) update)
    {
        try
        {
            var existingIdea = Ideas.FirstOrDefault(i => i.Id == update.IdeaId);
            if (existingIdea == null)
            {
                NotificationService.ShowError("Error updating idea: Idea not found.");
                return;
            }

            var trimmedDescription = update.Description.Trim();
            if (string.Equals(existingIdea.Description, trimmedDescription, StringComparison.Ordinal))
            {
                return;
            }

            _localIdeaUpdateIds.Add(existingIdea.Id);

            var updatedIdea = await IdeaService.UpdateAsync(new Idea
            {
                Id = existingIdea.Id,
                ProjectId = existingIdea.ProjectId,
                Description = trimmedDescription,
                SortOrder = existingIdea.SortOrder
            });

            updatedIdea.Job = existingIdea.Job;

            var ideaIndex = Ideas.FindIndex(i => i.Id == updatedIdea.Id);
            if (ideaIndex >= 0)
            {
                Ideas[ideaIndex] = updatedIdea;
            }

            StateHasChanged();
        }
        catch (Exception ex)
        {
            _localIdeaUpdateIds.Remove(update.IdeaId);
            NotificationService.ShowError($"Error updating idea: {ex.Message}");
        }
    }

    private async Task DeleteIdea(Guid ideaId)
    {
        try
        {
            await IdeaService.DeleteAsync(ideaId);
            await LoadIdeas(force: true);
        }
        catch (Exception ex)
        {
            NotificationService.ShowError($"Error deleting idea: {ex.Message}");
        }
    }

    private async Task CopyIdea((Guid ideaId, Guid targetProjectId) args)
    {
        try
        {
            var targetProject = await ProjectService.GetByIdAsync(args.targetProjectId);
            await IdeaService.CopyToProjectAsync(args.ideaId, args.targetProjectId);
            NotificationService.ShowSuccess($"Idea copied to {targetProject?.Name ?? "project"} successfully.");
        }
        catch (Exception ex)
        {
            NotificationService.ShowError($"Error copying idea: {ex.Message}");
        }
    }

    private async Task MoveIdea((Guid ideaId, Guid targetProjectId) args)
    {
        try
        {
            var targetProject = await ProjectService.GetByIdAsync(args.targetProjectId);
            await IdeaService.MoveToProjectAsync(args.ideaId, args.targetProjectId);
            await LoadIdeas(force: true);
            NotificationService.ShowSuccess($"Idea moved to {targetProject?.Name ?? "project"} successfully.");
        }
        catch (Exception ex)
        {
            NotificationService.ShowError($"Error moving idea: {ex.Message}");
        }
    }

    private async Task StartIdeasProcessing()
    {
        await StartIdeasProcessingWithOptions(new IdeaProcessingOptions
        {
            AutoCommitMode = Project?.AutoCommitMode ?? AutoCommitMode.Off,
            ProviderId = GetDefaultIdeasProcessingProviderId(),
            ModelId = GetDefaultIdeasProcessingModelId()
        });
    }

    private async Task StartIdeasProcessingWithOptions(IdeaProcessingOptions options)
    {
        _isTogglingIdeasProcessing = true;
        StateHasChanged();

        try
        {
            var hasRunningJob = HasActiveJobs;
            var autoCommitMode = options.AutoCommitMode;

            // Update auto-commit mode if changed
            if (Project != null && Project.AutoCommitMode != autoCommitMode)
            {
                Project.AutoCommitMode = autoCommitMode;
                await ProjectService.UpdateAsync(Project);
            }

            await IdeaService.StartProcessingAsync(ProjectId, options);
            IsIdeasProcessingActive = true;
            if (Project != null)
            {
                Project.IdeasProcessingProviderId = options.ProviderId;
                Project.IdeasProcessingModelId = string.IsNullOrWhiteSpace(options.ModelId) ? null : options.ModelId.Trim();
            }

            var autoCommit = autoCommitMode != AutoCommitMode.Off;
            var commitMessage = autoCommit ? " Auto-commit is enabled." : "";
            var queueMessage = hasRunningJob ? " New ideas will queue behind the current job." : string.Empty;
            var providerName = options.ProviderId.HasValue
                ? GetAllowedJobProviders().FirstOrDefault(provider => provider.Id == options.ProviderId.Value)?.Name
                : null;
            var providerMessage = providerName == null
                ? string.Empty
                : $" Queued ideas will use {providerName}{(string.IsNullOrWhiteSpace(options.ModelId) ? string.Empty : $" ({options.ModelId})")}.";
            NotificationService.ShowInfo(
                $"Ideas will be converted to jobs automatically.{queueMessage}{commitMessage}{providerMessage}",
                "Auto-Processing Started");
        }
        catch (Exception ex)
        {
            // Clear processing state on error
            ProcessingIdeaIds.Clear();
            NotificationService.ShowError($"Error starting Ideas processing: {ex.Message}");
        }
        finally
        {
            _isTogglingIdeasProcessing = false;
            StateHasChanged();
        }
    }

    private async Task StopIdeasProcessing()
    {
        _isTogglingIdeasProcessing = true;
        StateHasChanged();

        try
        {
            await IdeaService.StopProcessingAsync(ProjectId);
            IsIdeasProcessingActive = false;
            ProcessingIdeaIds.Clear();
            if (Project != null)
            {
                Project.IdeasProcessingProviderId = null;
                Project.IdeasProcessingModelId = null;
            }
            NotificationService.ShowInfo("Ideas auto-processing stopped.", "Processing Stopped");
        }
        catch (Exception ex)
        {
            NotificationService.ShowError($"Error stopping Ideas processing: {ex.Message}");
        }
        finally
        {
            _isTogglingIdeasProcessing = false;
            StateHasChanged();
        }
    }

    private async Task StartSingleIdea(Guid ideaId)
    {
        // Prevent double-add by checking if already processing
        var idea = Ideas.FirstOrDefault(i => i.Id == ideaId);
        if (idea == null || idea.JobId.HasValue || idea.IsProcessing || ProcessingIdeaIds.Contains(ideaId))
        {
            NotificationService.ShowWarning("This idea is already being processed.");
            return;
        }

        // Mark as processing immediately for UI feedback
        ProcessingIdeaIds.Add(ideaId);
        StateHasChanged();

        try
        {
            var job = await IdeaService.ConvertToJobAsync(ideaId);
            if (job != null)
            {
                await RefreshJobs();
                await LoadIdeas(force: true);
            }
            else
            {
                ProcessingIdeaIds.Remove(ideaId);
                NotificationService.ShowError("Failed to convert idea to job.");
            }
        }
        catch (Exception ex)
        {
            ProcessingIdeaIds.Remove(ideaId);
            NotificationService.ShowError($"Error starting idea: {ex.Message}");
        }
    }

    private async Task ExpandIdea((Guid ideaId, IdeaExpansionRequest request) args)
    {
        var ideaId = args.ideaId;
        var request = args.request;
        _expandingIdeaId = ideaId;

        // Cancel any previous expansion
        _expandCts?.Cancel();
        _expandCts?.Dispose();
        _expandCts = new CancellationTokenSource();
        var ct = _expandCts.Token;

        try
        {
            var result = await IdeaService.ExpandIdeaAsync(ideaId, request, ct);
            if (result != null)
            {
                await LoadIdeas(force: true);
                if (result.ExpansionStatus == IdeaExpansionStatus.PendingReview)
                {
                    NotificationService.ShowSuccess("Idea expanded successfully. Review the specification.", "AI Expansion Complete");
                }
                else if (result.ExpansionStatus == IdeaExpansionStatus.Failed)
                {
                    NotificationService.ShowError(result.ExpansionError ?? "Expansion failed", "AI Expansion Failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // User cancelled - try to reset the server-side status
            try
            {
                await IdeaService.CancelExpansionAsync(ideaId);
                await LoadIdeas(force: true);
            }
            catch { /* Best effort */ }
            NotificationService.ShowInfo("Expansion cancelled.", "Cancelled");
        }
        catch (Exception ex)
        {
            // On error, also cancel the server-side expansion status
            try
            {
                await IdeaService.CancelExpansionAsync(ideaId);
                await LoadIdeas(force: true);
            }
            catch { /* Best effort */ }
            NotificationService.ShowError($"Error expanding idea: {ex.Message}");
        }
        finally
        {
            _expandingIdeaId = null;
        }
    }

    private async Task CancelIdeaExpansion(Guid ideaId)
    {
        _expandCts?.Cancel();

        try
        {
            await IdeaService.CancelExpansionAsync(ideaId);
            await LoadIdeas(force: true);
            NotificationService.ShowInfo("Expansion cancelled.", "Cancelled");
        }
        catch (Exception ex)
        {
            NotificationService.ShowError($"Error cancelling expansion: {ex.Message}");
        }
        finally
        {
            _expandingIdeaId = null;
        }
    }

    private async Task ApproveIdeaExpansion((Guid ideaId, string? editedDescription) args)
    {
        try
        {
            var result = await IdeaService.ApproveExpansionAsync(args.ideaId, args.editedDescription);
            if (result != null)
            {
                await LoadIdeas(force: true);
                NotificationService.ShowSuccess("Specification approved. The idea is ready to run.", "Expansion Approved");
            }
        }
        catch (Exception ex)
        {
            NotificationService.ShowError($"Error approving expansion: {ex.Message}");
        }
    }

    private async Task RejectIdeaExpansion(Guid ideaId)
    {
        try
        {
            await IdeaService.RejectExpansionAsync(ideaId);
            await LoadIdeas(force: true);
            NotificationService.ShowInfo("Expansion discarded. You can try again or edit manually.", "Expansion Discarded");
        }
        catch (Exception ex)
        {
            NotificationService.ShowError($"Error rejecting expansion: {ex.Message}");
        }
    }

    private async Task SuggestIdeasFromCodebase()
    {
        // The panel handles the full suggestion flow and notifications.
        // This callback is invoked only on success so we reload the ideas list.
        var expectedTotalIdeas = Math.Max(IdeasTotalCount, 1);
        _ideasPageNumber = Math.Max(1, (expectedTotalIdeas + IdeasPageSize - 1) / IdeasPageSize);
        await LoadIdeas(force: true);
        StateHasChanged();
    }

    private async Task SaveEnvironments()
    {
        if (Project == null) return;

        _isSavingEnvironments = true;
        _environmentError = null;
        StateHasChanged();

        try
        {
            await ProjectService.UpdateAsync(Project);
            Project = await ProjectService.GetByIdAsync(ProjectId);
            NotificationService.ShowSuccess("Environments saved successfully.");
        }
        catch (Exception ex)
        {
            _environmentError = ex.Message;
        }
        finally
        {
            _isSavingEnvironments = false;
            StateHasChanged();
        }
    }

    private async Task LoadPreviousJobsPage()
    {
        if (_jobsPageNumber <= 1 || _isLoadingJobsPage)
        {
            return;
        }

        _jobsPageNumber--;
        await RefreshProjectJobsPage();
    }

    private async Task LoadNextJobsPage()
    {
        if (_isLoadingJobsPage || (_jobsPageNumber * ProjectJobsPageSize) >= JobsTotalCount)
        {
            return;
        }

        _jobsPageNumber++;
        await RefreshProjectJobsPage();
    }

    private void ClampJobsPageNumber(int totalJobsCount)
    {
        var maxPage = Math.Max(1, (int)Math.Ceiling(totalJobsCount / (double)ProjectJobsPageSize));
        if (_jobsPageNumber > maxPage)
        {
            _jobsPageNumber = maxPage;
        }
    }

    private async Task RefreshProjectJobsPage()
    {
        _isLoadingJobsPage = true;
        StateHasChanged();

        try
        {
            await RefreshJobs(force: true);
        }
        finally
        {
            _isLoadingJobsPage = false;
            StateHasChanged();
        }
    }

    private async Task LoadPreviousIdeasPage()
    {
        if (_ideasPageNumber <= 1 || _isLoadingIdeasPage)
        {
            return;
        }

        _ideasPageNumber--;
        await RefreshIdeasPage();
    }

    private async Task LoadNextIdeasPage()
    {
        if (_isLoadingIdeasPage || (_ideasPageNumber * IdeasPageSize) >= IdeasTotalCount)
        {
            return;
        }

        _ideasPageNumber++;
        await RefreshIdeasPage();
    }

    private async Task RefreshIdeasPage()
    {
        _isLoadingIdeasPage = true;
        StateHasChanged();

        try
        {
            await LoadIdeas(force: true);
        }
        finally
        {
            _isLoadingIdeasPage = false;
            StateHasChanged();
        }
    }

    private async Task LoadInferenceModels()
    {
        try
        {
            _availableInferenceProviders = (await InferenceProviderService.GetEnabledAsync()).ToList();
            _hasInference = _availableInferenceProviders.Any();

            if (_hasInference)
            {
                var health = await InferenceService.CheckHealthAsync();
                if (health.IsAvailable)
                {
                    _availableLocalModels = health.DiscoveredModels;
                }
            }
        }
        catch
        {
            // Non-critical - inference is optional
            _hasInference = false;
            _availableInferenceProviders = new();
            _availableLocalModels = new();
        }
    }

    private async Task LoadSuggestionProviderModels()
    {
        var enabledProviders = Providers.Where(provider => provider.IsEnabled).ToList();
        var tasks = enabledProviders.Select(async provider =>
        {
            provider.AvailableModels = (await ProviderService.GetModelsAsync(provider.Id))
                .Where(model => model.IsAvailable)
                .OrderByDescending(model => model.IsDefault)
                .ThenBy(model => model.DisplayName ?? model.ModelId)
                .ToList();
        });
        await Task.WhenAll(tasks);
    }

    private int GetProjectTotalInputTokens() => Jobs.Sum(j => j.InputTokens ?? 0);
    private int GetProjectTotalOutputTokens() => Jobs.Sum(j => j.OutputTokens ?? 0);
    private decimal GetProjectTotalCost() => Jobs.Sum(j => j.TotalCostUsd ?? 0);
    private bool HasProjectTokenData() => GetProjectTotalInputTokens() > 0 || GetProjectTotalOutputTokens() > 0 ||
    GetProjectTotalCost() > 0;

    private static string TruncateForToast(string text, int maxLength = 50)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text;
        return text[..(maxLength - 3)] + "...";
    }

    private void ShowEditProjectModal()
    {
        _showEditProjectModal = true;
    }

    private async Task ToggleProjectActive()
    {
        if (Project == null) return;
        try
        {
            Project.IsActive = !Project.IsActive;
            await ProjectService.UpdateAsync(Project);
            NotificationService.ShowSuccess($"{Project.Name} is now {(Project.IsActive ? "active" : "inactive")}.");
            StateHasChanged();
        }
        catch (Exception ex)
        {
            Project.IsActive = !Project.IsActive; // Revert on failure
            NotificationService.ShowError($"Error toggling project status: {ex.Message}");
        }
    }

    private async Task HandleProjectSaved(Guid? projectId)
    {
        _showEditProjectModal = false;
        // Reload project data to reflect changes
        await LoadData();
    }

    private void HandleProjectModalCancelled()
    {
        _showEditProjectModal = false;
    }

    private void HandleProjectModalClosed()
    {
        _showEditProjectModal = false;
    }

    private async Task HandleRepositoryCreated(string? gitHubRepository)
    {
        // Reload data after a repository is created (this updates Project.GitHubRepository in UI)
        await LoadData();
        await LoadGitInfo();
        NotificationService.ShowSuccess("GitHub repository created and linked successfully!");
    }

    private async Task StopAllActive()
    {
        _isStoppingAll = true;
        try
        {
            if (IsIdeasProcessingActive)
                await StopIdeasProcessing();
            if (_autoPilotStatus != null && !AutoPilotPanel.IsTerminal(_autoPilotStatus.Status))
            {
                try { await AutoPilotService.StopAsync(ProjectId); } catch { }
                try { _autoPilotStatus = await AutoPilotService.GetStatusAsync(ProjectId); } catch { }
                if (_autoPilotPanel != null)
                    await _autoPilotPanel.RefreshAsync();
            }
        }
        finally
        {
            _isStoppingAll = false;
        }
    }
}
