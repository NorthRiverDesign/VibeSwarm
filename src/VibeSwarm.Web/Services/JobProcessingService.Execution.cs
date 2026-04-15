using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.Utilities;

namespace VibeSwarm.Web.Services;

public partial class JobProcessingService
{
    private async Task ProcessJobAsync(
        Job job,
        IJobService jobService,
        IProviderService providerService,
        VibeSwarmDbContext dbContext,
        JobExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Processing job {JobId} for project {ProjectName} (Worker: {WorkerId})",
            job.Id, job.Project?.Name, _workerInstanceId);

        // Store provider ID for later cleanup
        executionContext.ProviderId = job.ProviderId;

        string? workingDirectory = null;
        string? projectMemoryFilePath = null;
        try
        {
            // Check if job was cancelled before we even started
            if (await jobService.IsCancellationRequestedAsync(job.Id, cancellationToken))
            {
                await ReleaseJobAsync(job.Id, JobStatus.Cancelled, "Cancelled before start", dbContext, cancellationToken);
                await NotifyJobCompletedAsync(job.Id, false, "Job was cancelled before processing started");
                return;
            }

            // Claim ownership before any provider work starts so duplicate schedulers cannot
            // launch the same CLI agent twice for a single job.
            var claimed = await ClaimJobAsync(job.Id, dbContext, cancellationToken);
            if (!claimed)
            {
                _logger.LogInformation("Skipping provider execution for job {JobId} because another worker already claimed it.", job.Id);
                return;
            }

            // Refresh execution plan from current project settings so queued jobs
            // pick up provider/model changes made after creation.
            try
            {
                await jobService.RefreshExecutionPlanAsync(job.Id, cancellationToken);
                await dbContext.Entry(job).ReloadAsync(cancellationToken);
                if (job.Provider == null)
                {
                    await dbContext.Entry(job).Reference(j => j.Provider).LoadAsync(cancellationToken);
                }
                if (job.Project == null)
                {
                    await dbContext.Entry(job).Reference(j => j.Project).LoadAsync(cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to refresh execution plan for job {JobId}, continuing with existing plan", job.Id);
            }

            // Store provider ID for cleanup (may have changed after refresh)
            executionContext.ProviderId = job.ProviderId;

            job.Status = JobStatus.Started;
            job.StartedAt ??= DateTime.UtcNow;
            job.LastActivityAt = DateTime.UtcNow;
            job.WorkerInstanceId = _workerInstanceId;
            job.LastHeartbeatAt = DateTime.UtcNow;
            job.NotBeforeUtc = null; // Clear any rate-limit backoff now that we're running
            await NotifyStatusChangedAsync(job.Id, JobStatus.Started);

            // Check again after status update - double-check for race conditions
            if (await jobService.IsCancellationRequestedAsync(job.Id, cancellationToken))
            {
                await ReleaseJobAsync(job.Id, JobStatus.Cancelled, "Job was cancelled", dbContext, cancellationToken);
                await NotifyJobCompletedAsync(job.Id, false, "Job was cancelled");
                return;
            }

            // Check if provider is available
            if (job.Provider == null)
            {
                await ReleaseJobAsync(job.Id, JobStatus.Failed, "Provider not found", dbContext, cancellationToken);
                await NotifyJobCompletedAsync(job.Id, false, "Provider not found");
                return;
            }

            // Create provider instance
            var provider = CreateProviderInstance(job.Provider);
            executionContext.ProviderInstance = provider;

            var providerValidationError = await ValidateProviderAvailabilityAsync(job.Id, job.Provider, provider, cancellationToken);
            if (!string.IsNullOrWhiteSpace(providerValidationError))
            {
                await ReleaseJobAsync(job.Id, JobStatus.Failed, providerValidationError, dbContext, cancellationToken);
                await NotifyJobCompletedAsync(job.Id, false, providerValidationError);
                return;
            }

            // Update status to processing
            var initialStatus = ShouldUsePlanningStage(job.Project) && string.IsNullOrWhiteSpace(job.PlanningOutput)
                ? JobStatus.Planning
                : JobStatus.Processing;
            await UpdateJobStatusAsync(job.Id, initialStatus, dbContext, cancellationToken);
            await NotifyStatusChangedAsync(job.Id, initialStatus);

            // Send initial activity notification
            var initialActivity = initialStatus == JobStatus.Planning
                ? "Preparing planning stage..."
                : "Initializing coding agent...";
            await UpdateHeartbeatAsync(job.Id, initialActivity, dbContext, cancellationToken);
            await NotifyJobActivityAsync(job.Id, initialActivity, DateTime.UtcNow);

            // Prepare the configured working branch before starting work (if this is a git repository)
            workingDirectory = job.Project?.WorkingPath;
            if (!string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory))
            {
                try
                {
                    var isGitRepo = await _versionControlService.IsGitRepositoryAsync(workingDirectory, cancellationToken);
                    if (isGitRepo)
                    {
                        var checkpointBaseBranch = await PreserveWorkingTreeBeforeBranchPreparationAsync(
                            job,
                            workingDirectory,
                            dbContext,
                            captureJobDiff: false,
                            reason: "Protected local changes before preparing the job branch.",
                            cancellationToken: cancellationToken);

                        var branchActivity = string.IsNullOrWhiteSpace(job.Branch)
                            ? "Syncing working branch..."
                            : $"Preparing branch '{job.Branch}'...";
                        await UpdateHeartbeatAsync(job.Id, branchActivity, dbContext, cancellationToken);
                        await NotifyJobActivityAsync(job.Id, branchActivity, DateTime.UtcNow);

                        await PrepareWorkingBranchAsync(job, workingDirectory, checkpointBaseBranch, cancellationToken);
                    }
                }
                catch (GitCheckpointRequiredException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error preparing git branch for job {JobId}. Continuing with local state.", job.Id);
                }
            }

            // Capture git commit hash before execution for diff comparison later
            if (!string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory))
            {
                try
                {
                    executionContext.GitCommitBefore = await _versionControlService.GetCurrentCommitHashAsync(workingDirectory, cancellationToken);
                    if (!string.IsNullOrEmpty(executionContext.GitCommitBefore))
                    {
                        _logger.LogInformation("Captured git commit {Commit} before job {JobId} execution",
                            executionContext.GitCommitBefore[..Math.Min(8, executionContext.GitCommitBefore.Length)], job.Id);

                        // Store commit hash in database
                        var jobForGit = await dbContext.Jobs.FindAsync(new object[] { job.Id }, cancellationToken);
                        if (jobForGit != null)
                        {
                            jobForGit.GitCommitBefore = executionContext.GitCommitBefore;
                            await dbContext.SaveChangesAsync(cancellationToken);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to capture git commit hash for job {JobId}", job.Id);
                }
            }

            // Auto-generate or refresh repo map if stale (>24 hours) or missing
            if (!string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory) && job.Project != null)
            {
                try
                {
                    if (job.Project.RepoMap == null || job.Project.RepoMapGeneratedAt == null ||
                        job.Project.RepoMapGeneratedAt < DateTime.UtcNow.AddHours(-24))
                    {
                        _logger.LogInformation("Generating repo map for project {ProjectName} (job {JobId})", job.Project.Name, job.Id);
                        var repoMap = RepoMapGenerator.GenerateRepoMap(workingDirectory);
                        if (repoMap != null)
                        {
                            var projectForMap = await dbContext.Projects.FindAsync(new object[] { job.Project.Id }, cancellationToken);
                            if (projectForMap != null)
                            {
                                projectForMap.RepoMap = repoMap;
                                projectForMap.RepoMapGeneratedAt = DateTime.UtcNow;
                                await dbContext.SaveChangesAsync(cancellationToken);
                                // Update the in-memory project reference
                                job.Project.RepoMap = repoMap;
                                job.Project.RepoMapGeneratedAt = projectForMap.RepoMapGeneratedAt;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to generate repo map for job {JobId}", job.Id);
                }
            }

            // Load app settings for prompt structuring and efficiency rules
            var appSettings = await dbContext.AppSettings.FirstOrDefaultAsync(cancellationToken);

            // Start a background task to monitor for cancellation requests and send heartbeats
            // Note: This task manages its own DbContext scopes to avoid disposal issues
            var cancellationMonitorTask = MonitorCancellationAndHeartbeatAsync(job.Id, executionContext, cancellationToken);

            async Task FailDuringPlanningAsync(string errorMessage)
            {
                executionContext.CancellationTokenSource?.Cancel();
                try { await cancellationMonitorTask; } catch { }
                await ReleaseJobAsync(job.Id, JobStatus.Failed, errorMessage, dbContext, cancellationToken);
                await NotifyJobCompletedAsync(job.Id, false, errorMessage);
            }

            // Execute the job with session support
            _logger.LogInformation("Starting provider execution for job {JobId} in directory {WorkingDir}",
                job.Id, workingDirectory ?? "(default)");

            // Multi-cycle execution support
            var effectiveMaxCycles = job.CycleMode == CycleMode.SingleCycle ? 1 : job.MaxCycles;
            var currentCycle = job.CurrentCycle;
            var sessionId = job.SessionId;
			ExecutionResult? lastResult = null;
			int? executionInputTokens = null;
			int? executionOutputTokens = null;
			decimal? executionCostUsd = null;

            // Track last progress update time to avoid excessive database writes
            var lastProgressUpdate = DateTime.MinValue;
            var progressUpdateInterval = TimeSpan.FromSeconds(2); // Update every 2 seconds to reduce database load
            var progressLock = new object();

            // Progress<T> doesn't properly handle async callbacks, so we use a synchronous handler
            // that fires updates in the background with proper scoping to avoid DbContext disposal issues
            var progress = new Progress<ExecutionProgress>(p =>
            {
                // Capture and store process ID and command as soon as they're reported
                if (p.ProcessId.HasValue && executionContext.ProcessId != p.ProcessId.Value)
                {
                    executionContext.ProcessId = p.ProcessId.Value;
                    executionContext.CommandUsed = p.CommandUsed;
                    _logger.LogInformation("Captured process ID {ProcessId} for job {JobId}. Command: {Command}",
                        p.ProcessId.Value, job.Id, p.CommandUsed ?? "(unknown)");

                    // Notify UI about process start with full command
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            if (_jobUpdateService != null)
                            {
                                await _jobUpdateService.NotifyProcessStarted(job.Id, p.ProcessId.Value,
                                    p.CommandUsed ?? $"{job.Provider?.Type} CLI");
                            }
                        }
                        catch { }
                    });

                    // Store process ID and command in database immediately
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var pidScope = _scopeFactory.CreateScope();
                            var pidDbContext = pidScope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
                            var jobForPid = await pidDbContext.Jobs.FindAsync(new object[] { job.Id });
                            if (jobForPid != null)
                            {
                                jobForPid.ProcessId = p.ProcessId.Value;
                                jobForPid.CommandUsed = p.CommandUsed;
                                if (jobForPid.Status == JobStatus.Planning)
                                {
                                    jobForPid.PlanningCommandUsed = p.CommandUsed;
                                }
                                else
                                {
                                    jobForPid.ExecutionCommandUsed = p.CommandUsed;
                                }
                                await pidDbContext.SaveChangesAsync(CancellationToken.None);
                                _logger.LogDebug("Stored process ID {ProcessId} and command in database for job {JobId}",
                                    p.ProcessId.Value, job.Id);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to store process ID/command for job {JobId}", job.Id);
                        }
                    });
                }

                // Log provider connection state changes
                if (!string.IsNullOrEmpty(p.OutputLine) && p.OutputLine.StartsWith("[Connection]"))
                {
                    _logger.LogInformation("Job {JobId} provider connection state: {State}", job.Id, p.OutputLine);
                }

                // Stream output lines to UI in real-time AND accumulate in buffer for storage
                if (!string.IsNullOrEmpty(p.OutputLine))
                {
                    // Accumulate output for database storage
                    executionContext.AppendOutput(p.OutputLine, p.IsErrorOutput);

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            if (_jobUpdateService != null)
                            {
                                await _jobUpdateService.NotifyJobOutput(job.Id, p.OutputLine, p.IsErrorOutput, DateTime.UtcNow);
                            }
                        }
                        catch { }
                    });

                    // Detect if the CLI is requesting user interaction
                    if (!executionContext.IsPausedForInteraction)
                    {
                        var interactionRequest = InteractionDetector.DetectInteraction(
                            p.OutputLine,
                            executionContext.GetRecentOutputLines());

                        if (interactionRequest != null && interactionRequest.IsInteractionRequested && interactionRequest.Confidence >= 0.70)
                        {
                            _logger.LogInformation(
                                "Interaction detected for job {JobId}: Type={Type}, Confidence={Confidence:P0}, Prompt={Prompt}",
                                job.Id, interactionRequest.Type, interactionRequest.Confidence, interactionRequest.Prompt);

                            // Mark context as paused
                            executionContext.IsPausedForInteraction = true;
                            executionContext.CurrentInteractionRequest = interactionRequest;

                            // Update database and notify UI in background
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    using var interactionScope = _scopeFactory.CreateScope();
                                    var interactionJobService = interactionScope.ServiceProvider.GetRequiredService<IJobService>();
                                    var interactionDbContext = interactionScope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();

                                    // Serialize choices if available
                                    string? choicesJson = interactionRequest.Choices != null && interactionRequest.Choices.Count > 0
                                        ? JsonSerializer.Serialize(interactionRequest.Choices)
                                        : null;

                                    // Update job status in database
                                    await interactionJobService.PauseForInteractionAsync(
                                        job.Id,
                                        interactionRequest.Prompt ?? interactionRequest.RawOutput ?? "Interaction required",
                                        interactionRequest.Type.ToString(),
                                        choicesJson,
                                        CancellationToken.None);

                                    // Notify UI
                                    if (_jobUpdateService != null)
                                    {
                                        await _jobUpdateService.NotifyJobInteractionRequired(
                                            job.Id,
                                            interactionRequest.Prompt ?? interactionRequest.RawOutput ?? "Interaction required",
                                            interactionRequest.Type.ToString(),
                                            interactionRequest.Choices,
                                            interactionRequest.DefaultResponse);
                                    }

                                    await NotifyStatusChangedAsync(job.Id, JobStatus.Paused);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "Failed to pause job {JobId} for interaction", job.Id);
                                    executionContext.IsPausedForInteraction = false;
                                    executionContext.CurrentInteractionRequest = null;
                                }
                            });
                        }
                    }

                    return; // Don't process output lines as activity updates
                }

                var activity = !string.IsNullOrEmpty(p.ToolName)
                    ? $"Running tool: {p.ToolName}"
                    : (p.IsStreaming ? "Processing..." : p.CurrentMessage ?? "Working...");

                _logger.LogDebug("Job {JobId} progress: {Activity}", job.Id, activity);

                // Throttle progress updates to avoid database overload
                var now = DateTime.UtcNow;
                bool shouldUpdate;
                lock (progressLock)
                {
                    shouldUpdate = now - lastProgressUpdate >= progressUpdateInterval;
                    if (shouldUpdate)
                    {
                        lastProgressUpdate = now;
                    }
                }

                if (shouldUpdate)
                {
                    // Fire async updates in the background with a NEW scope to avoid DbContext disposal
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // Create a new scope for this background operation
                            using var progressScope = _scopeFactory.CreateScope();
                            var scopedDbContext = progressScope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();

                            await UpdateHeartbeatAsync(job.Id, activity, scopedDbContext, CancellationToken.None);
                            await NotifyJobActivityAsync(job.Id, activity, now);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to update progress for job {JobId}", job.Id);
                        }
                    });
                }
                else
                {
                    // Still send SignalR notification for real-time UI updates, just skip database
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await NotifyJobActivityAsync(job.Id, activity, now);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to send activity notification for job {JobId}", job.Id);
                        }
                    });
                }
            });

            // ===== Multi-Cycle Execution Loop =====
            if (job.Project != null)
            {
                _projectEnvironmentCredentialService.PopulateForExecution(job.Project);
            }

            var jobEnvironmentVariables = _projectEnvironmentCredentialService.BuildJobEnvironmentVariables(job.Project);

            // Snapshot which environments and Playwright access were exposed to this job
            var environmentSnapshots = JobEnvironmentSnapshot.FromProject(job.Project);
            var hasWebEnvironment = environmentSnapshots.Any(e => e.Type == EnvironmentType.Web);
            job.PlaywrightEnabled = hasWebEnvironment;
            job.EnvironmentCount = environmentSnapshots.Count;
            if (environmentSnapshots.Count > 0)
            {
                job.EnvironmentsJson = System.Text.Json.JsonSerializer.Serialize(environmentSnapshots);
            }

            try
            {
                using var snapshotScope = _scopeFactory.CreateScope();
                var snapshotDbContext = snapshotScope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
                var jobForSnapshot = await snapshotDbContext.Jobs.FindAsync(new object[] { job.Id }, cancellationToken);
                if (jobForSnapshot != null)
                {
                    jobForSnapshot.PlaywrightEnabled = job.PlaywrightEnabled;
                    jobForSnapshot.EnvironmentCount = job.EnvironmentCount;
                    jobForSnapshot.EnvironmentsJson = job.EnvironmentsJson;
                    await snapshotDbContext.SaveChangesAsync(cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist environment snapshot for job {JobId}", job.Id);
            }

            var enableStructuring = appSettings?.EnablePromptStructuring ?? true;
            var enableCommitAttribution = appSettings?.EnableCommitAttribution ?? true;

            // Build system prompt rules for agent efficiency
            var injectEfficiencyRules = appSettings?.InjectEfficiencyRules ?? true;
            var injectRepoMap = appSettings?.InjectRepoMap ?? true;
            var isIdeaJob = await dbContext.Ideas
                .AsNoTracking()
                .AnyAsync(idea => idea.JobId == job.Id, cancellationToken);

            string? BuildExecutionSystemPromptRules(ProviderType providerType)
            {
                return isIdeaJob
                    ? PromptBuilder.BuildIdeaSystemPromptRules(job.Project, injectEfficiencyRules, injectRepoMap)
                    : PromptBuilder.BuildSystemPromptRules(
                        job.Project,
                        injectEfficiencyRules,
                        injectRepoMap,
                        providerType,
                        enableCommitAttribution);
            }

            var systemPromptRules = BuildExecutionSystemPromptRules(provider.Type);
            projectMemoryFilePath = await PrepareProjectMemoryFileAsync(job.Project, cancellationToken);
            var projectMemoryRules = PromptBuilder.BuildProjectMemoryRules(job.Project, projectMemoryFilePath);
            if (!string.IsNullOrWhiteSpace(projectMemoryRules))
            {
                systemPromptRules = string.IsNullOrWhiteSpace(systemPromptRules)
                    ? projectMemoryRules
                    : $"{systemPromptRules}{Environment.NewLine}{Environment.NewLine}{projectMemoryRules}";
            }

            // Inject role-specific system prompt context for team swarm jobs
            if (job.AgentId.HasValue)
            {
                try
                {
                    var agent = await dbContext.Agents
						.Include(role => role.SkillLinks)
							.ThenInclude(link => link.Skill)
                        .FirstOrDefaultAsync(r => r.Id == job.AgentId.Value, cancellationToken);
                    if (agent != null)
                    {
                        var swarmSize = job.SwarmId.HasValue
                            ? await dbContext.Jobs.CountAsync(j => j.SwarmId == job.SwarmId, cancellationToken)
                            : 1;
                        var roleContext = PromptBuilder.BuildRoleSystemPromptContext(agent, swarmSize);
                        if (!string.IsNullOrWhiteSpace(roleContext))
                        {
                            systemPromptRules = string.IsNullOrWhiteSpace(systemPromptRules)
                                ? roleContext
                                : $"{roleContext}{Environment.NewLine}{Environment.NewLine}{systemPromptRules}";
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load team role context for job {JobId}, continuing without it", job.Id);
                }
            }

            var planningOutput = job.PlanningOutput;
            if (ShouldUsePlanningStage(job.Project) && string.IsNullOrWhiteSpace(planningOutput))
            {
                await UpdateJobStatusAsync(job.Id, JobStatus.Planning, dbContext, cancellationToken);
                await NotifyStatusChangedAsync(job.Id, JobStatus.Planning);

                const string planningActivity = "Generating implementation plan...";
                await UpdateHeartbeatAsync(job.Id, planningActivity, dbContext, cancellationToken);
                await NotifyJobActivityAsync(job.Id, planningActivity, DateTime.UtcNow);

                var planningProviderId = job.Project!.PlanningProviderId!.Value;
                var planningProviderConfig = await providerService.GetByIdAsync(planningProviderId, cancellationToken);
                if (planningProviderConfig == null)
                {
                    await FailDuringPlanningAsync($"[Planning] Provider not found (ID: {planningProviderId}).");
                    return;
                }

                if (!ProviderPlanningHelper.SupportsPlanningMode(planningProviderConfig.Type))
                {
                    await FailDuringPlanningAsync($"[Planning] {planningProviderConfig.Name} does not support planning mode.");
                    return;
                }

                var planningProvider = CreateProviderInstance(planningProviderConfig);
                var planningValidationError = await ValidateProviderAvailabilityAsync(job.Id, planningProviderConfig, planningProvider, cancellationToken);
                if (!string.IsNullOrWhiteSpace(planningValidationError))
                {
                    await FailDuringPlanningAsync($"[Planning] {planningProviderConfig.Name}: {planningValidationError}");
                    return;
                }

				var planningMcpOptions = await GetMcpExecutionOptionsAsync(planningProviderId, job.Project, workingDirectory, cancellationToken);
				ExecutionResult planningResult;
				try
				{
					var planningSystemPromptRules = systemPromptRules;
					if (provider.Type != planningProviderConfig.Type)
					{
						planningSystemPromptRules = BuildExecutionSystemPromptRules(planningProviderConfig.Type);
						if (!string.IsNullOrWhiteSpace(projectMemoryRules))
						{
							planningSystemPromptRules = string.IsNullOrWhiteSpace(planningSystemPromptRules)
								? projectMemoryRules
								: $"{planningSystemPromptRules}{Environment.NewLine}{Environment.NewLine}{projectMemoryRules}";
						}
					}

					planningResult = await planningProvider.ExecuteWithOptionsAsync(
						ProviderPlanningHelper.BuildPlanningPrompt(planningProviderConfig.Type, job.GoalPrompt),
						new ExecutionOptions
						{
							WorkingDirectory = workingDirectory,
							McpConfigPath = planningMcpOptions.McpConfigPath,
							BashEnvPath = planningMcpOptions.BashEnvPath,
							AdditionalArgs = planningMcpOptions.AdditionalArgs,
							UseBareMode = planningProviderConfig.Type == ProviderType.Claude
								&& planningProviderConfig.ConnectionMode == ProviderConnectionMode.CLI
								&& ShouldUseClaudeBareMode(planningProviderConfig),
							Model = job.Project.PlanningModelId,
							ReasoningEffort = job.Project.PlanningReasoningEffort,
							Title = job.Title,
							AppendSystemPrompt = planningSystemPromptRules,
							EnvironmentVariables = jobEnvironmentVariables,
							DisallowedTools = ProviderPlanningHelper.PlanningDisallowedTools
						},
						progress,
						cancellationToken);
				}
				finally
				{
					CleanupMcpExecutionResources(planningMcpOptions.Resources);
				}

                await RecordUsageAndCheckExhaustionAsync(
                    planningProviderConfig.Id,
                    planningProviderConfig.Name,
                    job.Id,
                    planningResult,
                    planningProvider,
                    CancellationToken.None);

                planningOutput = ProviderPlanningHelper.ExtractExecutionText(planningResult);
                if (!planningResult.Success || string.IsNullOrWhiteSpace(planningOutput))
                {
                    var planningError = string.IsNullOrWhiteSpace(planningResult.ErrorMessage)
                        ? $"{planningProviderConfig.Name} did not return a plan."
                        : planningResult.ErrorMessage;
                    await FailDuringPlanningAsync($"[Planning] {planningProviderConfig.Name}: {planningError}");
                    return;
                }

                job.PlanningOutput = planningOutput.Trim();
                job.PlanningProviderId = planningProviderConfig.Id;
				job.PlanningModelUsed = planningResult.ModelUsed ?? job.Project.PlanningModelId;
				job.PlanningReasoningEffortUsed = job.Project.PlanningReasoningEffort;
				job.PlanningGeneratedAt = DateTime.UtcNow;
				job.PlanningInputTokens = planningResult.InputTokens;
				job.PlanningOutputTokens = planningResult.OutputTokens;
				job.PlanningCostUsd = planningResult.CostUsd;
				job.PlanningCommandUsed = executionContext.CommandUsed ?? planningResult.CommandUsed;

                // Clear planning phase's command/process so execution phase starts clean
                job.CommandUsed = null;
                job.ProcessId = null;
                executionContext.ProcessId = 0;
                executionContext.CommandUsed = null;

                await dbContext.SaveChangesAsync(cancellationToken);
            }

            var currentPrompt = PromptBuilder.BuildExecutionPrompt(job, planningOutput, enableStructuring);
            var attachedFiles = DeserializeAttachedFiles(job.AttachedFilesJson);
            var cycleComplete = false;
            await UpdateJobStatusAsync(job.Id, JobStatus.Processing, dbContext, cancellationToken);
            await NotifyStatusChangedAsync(job.Id, JobStatus.Processing);

            while (currentCycle <= effectiveMaxCycles && !cycleComplete && !cancellationToken.IsCancellationRequested)
            {
                if (effectiveMaxCycles > 1)
                {
                    _logger.LogInformation("Starting cycle {Current}/{Max} for job {JobId}",
                        currentCycle, effectiveMaxCycles, job.Id);

                    // Notify UI about cycle progress
                    if (_jobUpdateService != null)
                    {
                        await _jobUpdateService.NotifyJobCycleProgress(job.Id, currentCycle, effectiveMaxCycles);
                    }

                    // Update current cycle in database
                    using var cycleScope = _scopeFactory.CreateScope();
                    var cycleDbContext = cycleScope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
                    var jobForCycle = await cycleDbContext.Jobs.FindAsync(new object[] { job.Id }, cancellationToken);
                    if (jobForCycle != null)
                    {
                        jobForCycle.CurrentCycle = currentCycle;
                        await cycleDbContext.SaveChangesAsync(cancellationToken);
                    }
                }

                // Determine session ID for this cycle
                var cycleSessionId = job.CycleSessionMode == CycleSessionMode.ContinueSession ? sessionId : null;

                if (currentCycle == job.CurrentCycle)
                {
                    await RecordProviderAttemptAsync(
                        job.Id,
                        job.ProviderId,
                        job.Provider?.Name ?? provider.Name,
                        job.ModelUsed,
                        job.ActiveExecutionIndex,
                        "initial-execution",
                        dbContext,
                        cancellationToken);
                }

				var mcpOptions = await GetMcpExecutionOptionsAsync(job.ProviderId, job.Project, workingDirectory, cancellationToken);

				ExecutionResult result;
				try
				{
					result = await provider.ExecuteWithOptionsAsync(
						currentPrompt,
						new ExecutionOptions
						{
							SessionId = cycleSessionId,
							WorkingDirectory = workingDirectory,
							McpConfigPath = mcpOptions.McpConfigPath,
							BashEnvPath = mcpOptions.BashEnvPath,
							AdditionalArgs = mcpOptions.AdditionalArgs,
							UseBareMode = provider.Type == ProviderType.Claude
								&& provider.ConnectionMode == ProviderConnectionMode.CLI
								&& ShouldUseClaudeBareMode(job.Provider!),
							Model = job.ModelUsed,
							ReasoningEffort = job.ReasoningEffort,
							Title = job.Title,
							AttachedFiles = attachedFiles,
							AppendSystemPrompt = systemPromptRules,
							EnvironmentVariables = jobEnvironmentVariables
						},
						progress,
						cancellationToken);
				}
				finally
				{
					CleanupMcpExecutionResources(mcpOptions.Resources);
				}

                // Store last result and accumulate tokens/cost
                lastResult = result;
                sessionId = result.SessionId ?? sessionId;
				if (result.InputTokens.HasValue)
					executionInputTokens = (executionInputTokens ?? 0) + result.InputTokens.Value;
				if (result.OutputTokens.HasValue)
					executionOutputTokens = (executionOutputTokens ?? 0) + result.OutputTokens.Value;
				if (result.CostUsd.HasValue)
					executionCostUsd = (executionCostUsd ?? 0) + result.CostUsd.Value;
                if (!string.IsNullOrWhiteSpace(result.CommandUsed))
                {
                    job.ExecutionCommandUsed = result.CommandUsed;
                }

                // Check for cycle completion conditions
                if (!result.Success)
                {
                    _logger.LogWarning("Cycle {Current} failed for job {JobId}: {Error}",
                        currentCycle, job.Id, result.ErrorMessage);
                    cycleComplete = true;
                    break;
                }

                // Check cancellation between cycles
                using var cancelCheckScope = _scopeFactory.CreateScope();
                var cancelCheckService = cancelCheckScope.ServiceProvider.GetRequiredService<IJobService>();
                if (await cancelCheckService.IsCancellationRequestedAsync(job.Id, CancellationToken.None))
                {
                    _logger.LogInformation("Job {JobId} cancelled between cycles", job.Id);
                    cycleComplete = true;
                    break;
                }

                // Determine if we should continue cycling
                if (job.CycleMode == CycleMode.SingleCycle || currentCycle >= effectiveMaxCycles)
                {
                    cycleComplete = true;
                }
                else if (job.CycleMode == CycleMode.Autonomous)
                {
                    // Check if output contains CYCLE_COMPLETE marker
                    if (result.Output?.Contains("CYCLE_COMPLETE", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        _logger.LogInformation("Job {JobId} completed autonomously at cycle {Current}",
                            job.Id, currentCycle);
                        cycleComplete = true;
                    }
                    else
                    {
                        // Build next cycle prompt for autonomous mode
                        currentPrompt = string.IsNullOrWhiteSpace(job.CycleReviewPrompt)
                            ? "Review all changes made so far. Verify the code compiles and tests pass. If the task is complete and working correctly, respond with CYCLE_COMPLETE. Otherwise, continue implementing the remaining work."
                            : job.CycleReviewPrompt;
                        currentCycle++;
                    }
                }
                else if (job.CycleMode == CycleMode.FixedCount)
                {
                    // Build next cycle prompt for fixed count mode
                    currentPrompt = $"Continue implementing the task. This is cycle {currentCycle + 1} of {effectiveMaxCycles}. Review the current state and continue where you left off.";
                    currentCycle++;
                }
            }

			// Use accumulated results
			var finalResult = lastResult ?? new ExecutionResult { Success = false, ErrorMessage = "No execution result" };
			finalResult.InputTokens = executionInputTokens;
			finalResult.OutputTokens = executionOutputTokens;
			finalResult.CostUsd = executionCostUsd;

            // Stop monitoring cancellation
            executionContext.CancellationTokenSource?.Cancel();
            try { await cancellationMonitorTask; } catch { }

            // Re-fetch job state from database to check cancellation
            using var checkScope = _scopeFactory.CreateScope();
            var checkJobService = checkScope.ServiceProvider.GetRequiredService<IJobService>();
            var wasCancelled = await checkJobService.IsCancellationRequestedAsync(job.Id, CancellationToken.None);

            if (wasCancelled)
            {
                await UpdateProviderAttemptOutcomeAsync(job.Id, job.ActiveExecutionIndex, false, finalResult.ModelUsed ?? job.ModelUsed, dbContext, CancellationToken.None);
                var providerDisplayName = job.Provider?.Name;
                providerDisplayName ??= provider?.Name;
                providerDisplayName ??= "Unknown Provider";
                await CompleteJobAsync(job.Id, JobStatus.Cancelled, finalResult.SessionId, finalResult.Output,
                    "Job was cancelled by user", finalResult.InputTokens, finalResult.OutputTokens, finalResult.CostUsd, finalResult.ModelUsed,
                    executionContext, workingDirectory, dbContext, CancellationToken.None);

                // Record usage even for cancelled jobs
                await RecordUsageAndCheckExhaustionAsync(job.ProviderId, providerDisplayName, job.Id, finalResult, provider!, CancellationToken.None);

                await NotifyJobCompletedAsync(job.Id, false, "Job was cancelled by user");

                _logger.LogInformation("Job {JobId} was cancelled during execution", job.Id);
            }
            else if (finalResult.Success)
            {
                await UpdateProviderAttemptOutcomeAsync(job.Id, job.ActiveExecutionIndex, true, finalResult.ModelUsed ?? job.ModelUsed, dbContext, CancellationToken.None);
                var providerDisplayName = job.Provider?.Name;
                providerDisplayName ??= provider?.Name;
                providerDisplayName ??= "Unknown Provider";
                // Save messages
                if (finalResult.Messages.Count > 0)
                {
                    var messages = finalResult.Messages.Select(m => new JobMessage
                    {
                        Role = ParseMessageRole(m.Role),
                        Content = m.Content,
                        ToolName = m.ToolName,
                        ToolInput = m.ToolInput,
                        ToolOutput = m.ToolOutput,
                        CreatedAt = m.Timestamp
                    });

                    await checkJobService.AddMessagesAsync(job.Id, messages, CancellationToken.None);
                    await NotifyJobMessageAddedAsync(job.Id);
                }

                var hasGitChanges = await CompleteJobAsync(job.Id, JobStatus.Completed, finalResult.SessionId, finalResult.Output,
                    null, finalResult.InputTokens, finalResult.OutputTokens, finalResult.CostUsd, finalResult.ModelUsed,
                    executionContext, workingDirectory, dbContext, CancellationToken.None);

                if (hasGitChanges)
                {
                    await NotifyJobGitDiffUpdatedAsync(job.Id, true);
                }

                // Record usage after successful completion
                await RecordUsageAndCheckExhaustionAsync(job.ProviderId, providerDisplayName, job.Id, finalResult, provider!, CancellationToken.None);

                _logger.LogInformation("Job {JobId} completed successfully. Session: {SessionId}, InputTokens: {InputTokens}, OutputTokens: {OutputTokens}, Cost: {CostUsd}",
                    job.Id, finalResult.SessionId, finalResult.InputTokens, finalResult.OutputTokens, finalResult.CostUsd);
                await NotifyJobCompletedAsync(job.Id, true);
            }
            else
            {
                await UpdateProviderAttemptOutcomeAsync(job.Id, job.ActiveExecutionIndex, false, finalResult.ModelUsed ?? job.ModelUsed, dbContext, CancellationToken.None);
                var providerDisplayName = job.Provider?.Name;
                providerDisplayName ??= provider?.Name;
                providerDisplayName ??= "Unknown Provider";

                // Record usage even for failed jobs
                await RecordUsageAndCheckExhaustionAsync(job.ProviderId, providerDisplayName, job.Id, finalResult, provider!, CancellationToken.None);

                // Rate limit detection: if the provider reported a rate limit, re-queue the job
                // instead of marking it as failed. The circuit breaker cooldown respects the
                // provider's reset time (can be hours for GitHub Copilot).
                var isRateLimited = finalResult.IsSystemError &&
                    finalResult.DetectedUsageLimits is { IsLimitReached: true } &&
                    (finalResult.DetectedUsageLimits.LimitType is UsageLimitType.RateLimit or UsageLimitType.PremiumRequests);

                if (isRateLimited && _healthTracker != null)
                {
                    var limits = finalResult.DetectedUsageLimits!;
                    _healthTracker.RecordRateLimitFailure(job.ProviderId, finalResult.ErrorMessage, limits.ResetTime);

                    var resetDescription = limits.ResetTime.HasValue
                        ? $"Resets at {limits.ResetTime.Value:u}"
                        : "Reset time unknown";

                    // Check whether any alternative provider can pick up this job.
                    // If not, set a backoff time so the job doesn't spin in the queue.
                    var hasAlternativeProvider = await HasAlternativeProviderAsync(
                        job.ProjectId, job.ProviderId, dbContext, CancellationToken.None);

                    DateTime? backoffUntil = null;
                    if (!hasAlternativeProvider)
                    {
                        backoffUntil = limits.ResetTime ?? DateTime.UtcNow + TimeSpan.FromMinutes(30);
                        _logger.LogInformation(
                            "No alternative provider for job {JobId}. Backing off until {BackoffUntil:u}",
                            job.Id, backoffUntil);
                    }

                    // Re-queue the job instead of failing it — don't consume a retry attempt
                    await RequeueJobForRateLimitAsync(job.Id, job.ProviderId, providerDisplayName, resetDescription,
                        finalResult, executionContext, workingDirectory, backoffUntil, dbContext, CancellationToken.None);

                    _logger.LogWarning("Job {JobId} hit rate limit on provider {ProviderId}. {ResetDesc}. Re-queued for later execution",
                        job.Id, job.ProviderId, resetDescription);

                    if (_jobUpdateService != null)
                    {
                        var backoffMessage = hasAlternativeProvider
                            ? $"Rate limited. {resetDescription}. Jobs will try alternative providers."
                            : $"Rate limited. {resetDescription}. No alternative provider configured — jobs will back off until reset.";
                        await _jobUpdateService.NotifyProviderRateLimited(job.ProviderId, providerDisplayName,
                            backoffMessage, limits.ResetTime);
                    }
                    await NotifyStatusChangedAsync(job.Id, JobStatus.New);
                }
                else
                {
                    await CompleteJobAsync(job.Id, JobStatus.Failed, finalResult.SessionId, finalResult.Output,
                        finalResult.ErrorMessage, finalResult.InputTokens, finalResult.OutputTokens, finalResult.CostUsd, finalResult.ModelUsed,
                        executionContext, workingDirectory, dbContext, CancellationToken.None);

                    // System-level errors (model unavailable, upstream outages) should immediately
                    // trip the circuit breaker to prevent cascading failures on queued jobs
                    if (finalResult.IsSystemError && _healthTracker != null)
                    {
                        _healthTracker.RecordSystemFailure(job.ProviderId, finalResult.ErrorMessage);
                        _logger.LogWarning("Job {JobId} failed with system error, circuit breaker tripped for provider {ProviderId}: {Error}",
                            job.Id, job.ProviderId, finalResult.ErrorMessage);
                    }

                    _logger.LogWarning("Job {JobId} failed: {Error}. InputTokens: {InputTokens}, OutputTokens: {OutputTokens}, Cost: {CostUsd}",
                        job.Id, finalResult.ErrorMessage, finalResult.InputTokens, finalResult.OutputTokens, finalResult.CostUsd);
                    await NotifyJobCompletedAsync(job.Id, false, finalResult.ErrorMessage);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Job {JobId} was cancelled, resetting for potential retry", job.Id);
            try
            {
                using var resetScope = _scopeFactory.CreateScope();
                var resetDbContext = resetScope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
                var jobEntity = await resetDbContext.Jobs.FindAsync(job.Id);
                if (jobEntity != null)
                {
                    if (!string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory))
                    {
                        try
                        {
                            await PreserveWorkingTreeBeforeBranchPreparationAsync(
                                jobEntity,
                                workingDirectory,
                                resetDbContext,
                                captureJobDiff: true,
                                reason: jobEntity.CancellationRequested
                                    ? "Preserved local changes after the job was cancelled."
                                    : "Preserved local changes after the worker shut down during execution.",
                                cancellationToken: CancellationToken.None);
                        }
                        catch (Exception checkpointEx)
                        {
                            _logger.LogWarning(checkpointEx, "Failed to preserve local changes for cancelled job {JobId}", job.Id);
                        }
                    }

                    if (jobEntity.CancellationRequested)
                    {
                        // User requested cancellation
                        JobStateMachine.TryTransition(jobEntity, JobStatus.Cancelled, "Job was cancelled by user.");
                        jobEntity.ErrorMessage = "Job was cancelled by user";
                    }
                    else
                    {
                        // Service shutdown or timeout - reset for retry
                        JobStateMachine.TryTransition(jobEntity, JobStatus.New, "Service shutdown during execution. Queued for retry.");
                        jobEntity.ErrorMessage = jobEntity.GitCheckpointStatus == GitCheckpointStatus.Preserved
                            ? "Service shutdown during execution. Queued for retry after preserving local changes."
                            : "Service shutdown during execution. Queued for retry.";
                    }
                    jobEntity.WorkerInstanceId = null;
                    jobEntity.LastHeartbeatAt = null;
                    jobEntity.ProcessId = null;
                    jobEntity.CurrentActivity = null;
                    await resetDbContext.SaveChangesAsync(CancellationToken.None);
                }
            }
            catch (Exception resetEx)
            {
                _logger.LogError(resetEx, "Failed to reset job {JobId} after cancellation", job.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing job {JobId}", job.Id);
            try
            {
                using var errorScope = _scopeFactory.CreateScope();
                var errorDbContext = errorScope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
                await ReleaseJobAsync(job.Id, JobStatus.Failed, ex.Message, errorDbContext, CancellationToken.None);
            }
            catch { }
            await NotifyJobCompletedAsync(job.Id, false, ex.Message);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(projectMemoryFilePath))
            {
                try
                {
                    await PersistProjectMemoryAsync(job.Project?.Id, projectMemoryFilePath, CancellationToken.None);
                }
                catch (Exception memoryEx)
                {
                    _logger.LogWarning(memoryEx, "Failed to persist project memory for job {JobId}", job.Id);
                }
            }

            // Dispose SDK providers that implement IAsyncDisposable
            if (executionContext.ProviderInstance is IAsyncDisposable disposable)
            {
                try
                {
                    await disposable.DisposeAsync();
                }
                catch (Exception disposeEx)
                {
                    _logger.LogWarning(disposeEx, "Error disposing provider for job {JobId}", job.Id);
                }
            }
        }
    }

    private static List<string>? DeserializeAttachedFiles(string? attachedFilesJson)
    {
        if (string.IsNullOrWhiteSpace(attachedFilesJson))
        {
            return null;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<string>>(attachedFilesJson);
            var normalized = parsed?
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();

            return normalized is { Count: > 0 } ? normalized : null;
        }
        catch
        {
            return null;
        }
    }
}
