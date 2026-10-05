using Microsoft.Extensions.Logging;
using System.Text;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Inference;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.VersionControl.Models;
using VibeSwarm.Shared;

namespace VibeSwarm.Web.Services;

public partial class JobProcessingService
{
    /// <summary>
    /// Performs auto-commit (and optionally push) based on project settings.
    /// </summary>
    private async Task PerformAutoCommitAsync(
        Job job,
        string workingDirectory,
        bool enableCommitAttribution,
        CancellationToken cancellationToken)
    {
        try
        {
            var shouldCreatePullRequest = ShouldCreatePullRequest(job);

            // Check if there are uncommitted changes
            var hasChanges = await _versionControlService.HasUncommittedChangesAsync(workingDirectory, cancellationToken);
            if (!hasChanges)
            {
                // No uncommitted changes — the agent may have committed changes itself.
                // If the HEAD has moved since the job started, record the current HEAD hash
                // so the UI knows the changes are committed.
                if (!string.IsNullOrEmpty(job.GitCommitBefore))
                {
                    var currentHash = await _versionControlService.GetCurrentCommitHashAsync(workingDirectory, cancellationToken);
                    if (!string.IsNullOrEmpty(currentHash) &&
                        !string.Equals(currentHash, job.GitCommitBefore, StringComparison.OrdinalIgnoreCase))
                    {
                        job.GitCommitHash = currentHash;
                        _logger.LogInformation(
                            "Agent already committed changes for job {JobId}. Recorded HEAD {CommitHash} as GitCommitHash.",
                            job.Id, currentHash[..Math.Min(8, currentHash.Length)]);

                        // Determine effective commit mode: use project setting, or default to CommitOnly for IdeasAutoCommit
                        var effectiveMode = shouldCreatePullRequest
                            ? AutoCommitMode.CommitAndPush
                            : job.Project!.AutoCommitMode != AutoCommitMode.Off
                            ? job.Project.AutoCommitMode
                            : AutoCommitMode.CommitOnly;

                        // Push if configured
                        if (effectiveMode == AutoCommitMode.CommitAndPush)
                        {
                            await PushJobCommitAsync(job, workingDirectory, cancellationToken);
                        }
                    }
                    else
                    {
                        _logger.LogDebug("No uncommitted or committed changes to auto-commit for job {JobId}", job.Id);
                    }
                }
                else
                {
                    _logger.LogDebug("No uncommitted changes to auto-commit for job {JobId}", job.Id);
                }
                return;
            }

            // Determine effective commit mode: use project setting, or default to CommitOnly for IdeasAutoCommit
            var effectiveCommitMode = shouldCreatePullRequest
                ? AutoCommitMode.CommitAndPush
                : job.Project!.AutoCommitMode != AutoCommitMode.Off
                ? job.Project.AutoCommitMode
                : AutoCommitMode.CommitOnly;

            var commitMessage = await BuildCommitMessageAsync(job, workingDirectory, cancellationToken);
            var commitOptions = CommitAttributionHelper.BuildGitCommitOptions(job.Provider?.Type, enableCommitAttribution);

            _logger.LogInformation("Auto-committing changes for job {JobId} with mode {Mode}",
                job.Id, effectiveCommitMode);

            var commitResult = await _versionControlService.CommitAllChangesAsync(
                workingDirectory,
                commitMessage,
                cancellationToken,
                commitOptions);

            if (commitResult.Success)
            {
                job.GitCommitHash = commitResult.CommitHash;
                _logger.LogInformation("Auto-committed changes for job {JobId}: {CommitHash}",
                    job.Id, commitResult.CommitHash?[..Math.Min(8, commitResult.CommitHash?.Length ?? 0)]);

                // Push if configured
                if (effectiveCommitMode == AutoCommitMode.CommitAndPush)
                {
                    await PushJobCommitAsync(job, workingDirectory, cancellationToken);
                }
            }
            else
            {
                _logger.LogWarning("Auto-commit failed for job {JobId}: {Error}", job.Id, commitResult.Error);
                job.ErrorMessage = CombineNotices(job.ErrorMessage, $"The changes could not be committed: {commitResult.Error}");
            }
        }
        catch (Exception ex)
        {
            // Auto-commit failures should not fail the job
            _logger.LogWarning(ex, "Error during auto-commit for job {JobId}", job.Id);
        }
    }

    private const int MaxPushAttempts = 3;

    /// <summary>
    /// Pushes the job's commit. When origin moved on while the job ran, the commit is replayed on
    /// top of it and pushed again, so the agent never has to deal with the merge. A commit that
    /// conflicts with origin is kept on a recovery branch. Either way the job records what happened,
    /// because a commit that only exists on this machine is not delivered.
    /// </summary>
    private async Task PushJobCommitAsync(Job job, string workingDirectory, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var pushResult = await _versionControlService.PushAsync(workingDirectory, cancellationToken: cancellationToken);
            if (pushResult.Success)
            {
                _logger.LogInformation("Auto-pushed changes for job {JobId}", job.Id);
                return;
            }

            var rejected = pushResult.Error?.StartsWith("Push was rejected", StringComparison.Ordinal) == true;
            if (!rejected || attempt >= MaxPushAttempts)
            {
                _logger.LogWarning("Auto-push failed for job {JobId}: {Error}. Changes were committed but not pushed.", job.Id, pushResult.Error);
                job.ErrorMessage = CombineNotices(job.ErrorMessage, $"Committed, but the push failed: {pushResult.Error} The commit is still on the local branch and goes out with the next push.");
                return;
            }

            _logger.LogInformation("Origin moved on during job {JobId}; replaying its commit on top before pushing again", job.Id);
            var syncResult = await _versionControlService.SyncWithOriginAsync(workingDirectory, cancellationToken: cancellationToken);
            if (!syncResult.Success)
            {
                _logger.LogWarning("Could not catch up with origin for job {JobId}: {Error}", job.Id, syncResult.Error);
                job.ErrorMessage = CombineNotices(job.ErrorMessage, $"Committed, but origin moved on and the commit could not be replayed on top of it: {syncResult.Error}");
                return;
            }

            if (syncResult.RecoveryBranch != null)
            {
                _logger.LogWarning("Commit for job {JobId} conflicts with origin; kept on {RecoveryBranch}", job.Id, syncResult.RecoveryBranch);
                job.ErrorMessage = CombineNotices(job.ErrorMessage, $"Committed, but origin changed the same code while the job ran, so the commit was not pushed. It is kept on the branch {syncResult.RecoveryBranch}.");
                return;
            }

            job.GitCommitHash = syncResult.CommitHash ?? job.GitCommitHash;
        }
    }

    /// <summary>
    /// When re-applying a follow-up's earlier work left conflict markers, checks that the agent
    /// resolved every one. Returns false (and records why) if any marker is still in place.
    /// </summary>
    private async Task<bool> VerifyRestoreConflictsResolvedAsync(
        Job job,
        string workingDirectory,
        JobWorkRestoreResult? priorWorkRestore,
        CancellationToken cancellationToken)
    {
        if (_workSnapshots == null || priorWorkRestore is not { ConflictedFiles.Count: > 0 })
        {
            return true;
        }

        var unresolved = await _workSnapshots.FindUnresolvedConflictsAsync(workingDirectory, priorWorkRestore.ConflictedFiles, cancellationToken);
        if (unresolved.Count == 0)
        {
            return true;
        }

        _logger.LogWarning("Job {JobId} left merge conflict markers in {Files}", job.Id, string.Join(", ", unresolved));
        job.BuildVerified = false;
        job.BuildOutput = "Re-applying this job's earlier work conflicted with newer changes on the branch, " +
            "and these files still contain conflict markers (<<<<<<< / >>>>>>>):" + Environment.NewLine +
            string.Join(Environment.NewLine, unresolved.Select(file => $"- {file}")) + Environment.NewLine +
            "The changes were not committed. Send a follow-up asking the agent to resolve them.";
        return false;
    }

    /// <summary>
    /// Runs the project's configured build and test commands to verify the agent's changes compile and pass tests.
    /// Returns true if verification passed (or was not enabled), false if the build/tests failed.
    /// </summary>
    private async Task<bool> VerifyBuildAsync(Job job, string workingDirectory, CancellationToken cancellationToken)
    {
        var project = job.Project;
        if (project == null || !project.BuildVerificationEnabled || string.IsNullOrWhiteSpace(project.BuildCommand))
        {
            return true;
        }

        var outputBuilder = new StringBuilder();

        try
        {
            _logger.LogInformation("Running build verification for job {JobId} in {WorkingDirectory}", job.Id, workingDirectory);

            var buildResult = await RunShellCommandAsync(project.BuildCommand.Trim(), workingDirectory, cancellationToken);
            outputBuilder.AppendLine($"=== Build Command: {project.BuildCommand.Trim()} ===");
            outputBuilder.AppendLine($"Exit Code: {buildResult.ExitCode}");
            if (!string.IsNullOrWhiteSpace(buildResult.Output))
            {
                outputBuilder.AppendLine(buildResult.Output);
            }
            if (!string.IsNullOrWhiteSpace(buildResult.Error))
            {
                outputBuilder.AppendLine(buildResult.Error);
            }

            if (buildResult.ExitCode != 0)
            {
                _logger.LogWarning("Build verification FAILED for job {JobId}. Build command exited with code {ExitCode}",
                    job.Id, buildResult.ExitCode);
                job.BuildVerified = false;
                job.BuildOutput = TruncateBuildOutput(outputBuilder.ToString());
                return false;
            }

            _logger.LogInformation("Build command succeeded for job {JobId}", job.Id);

            // Run test command if configured
            if (!string.IsNullOrWhiteSpace(project.TestCommand))
            {
                var testResult = await RunShellCommandAsync(project.TestCommand.Trim(), workingDirectory, cancellationToken);
                outputBuilder.AppendLine();
                outputBuilder.AppendLine($"=== Test Command: {project.TestCommand.Trim()} ===");
                outputBuilder.AppendLine($"Exit Code: {testResult.ExitCode}");
                if (!string.IsNullOrWhiteSpace(testResult.Output))
                {
                    outputBuilder.AppendLine(testResult.Output);
                }
                if (!string.IsNullOrWhiteSpace(testResult.Error))
                {
                    outputBuilder.AppendLine(testResult.Error);
                }

                if (testResult.ExitCode != 0)
                {
                    _logger.LogWarning("Test verification FAILED for job {JobId}. Test command exited with code {ExitCode}",
                        job.Id, testResult.ExitCode);
                    job.BuildVerified = false;
                    job.BuildOutput = TruncateBuildOutput(outputBuilder.ToString());
                    return false;
                }

                _logger.LogInformation("Test command succeeded for job {JobId}", job.Id);
            }

            job.BuildVerified = true;
            job.BuildOutput = TruncateBuildOutput(outputBuilder.ToString());
            return true;
        }
        catch (OperationCanceledException)
        {
            outputBuilder.AppendLine("Build verification was cancelled.");
            job.BuildVerified = false;
            job.BuildOutput = TruncateBuildOutput(outputBuilder.ToString());
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Build verification encountered an error for job {JobId}", job.Id);
            outputBuilder.AppendLine($"Build verification error: {ex.Message}");
            job.BuildVerified = false;
            job.BuildOutput = TruncateBuildOutput(outputBuilder.ToString());
            return false;
        }
    }

    private async Task CreatePullRequestIfConfiguredAsync(Job job, string workingDirectory, CancellationToken cancellationToken)
    {
        if (!ShouldCreatePullRequest(job) || !string.IsNullOrWhiteSpace(job.PullRequestUrl))
        {
            return;
        }

        var targetBranch = GetEffectiveTargetBranch(job);
        if (string.IsNullOrWhiteSpace(targetBranch))
        {
            _logger.LogWarning("Job {JobId} requested pull-request delivery but no target branch was configured.", job.Id);
            return;
        }

        var sourceBranch = string.IsNullOrWhiteSpace(job.Branch)
            ? await _versionControlService.GetCurrentBranchAsync(workingDirectory, cancellationToken)
            : job.Branch;

        if (string.IsNullOrWhiteSpace(sourceBranch))
        {
            _logger.LogWarning("Job {JobId} requested pull-request delivery but the current branch could not be determined.", job.Id);
            return;
        }

        if (string.Equals(sourceBranch, targetBranch, StringComparison.Ordinal))
        {
            _logger.LogWarning("Job {JobId} requested pull-request delivery but source and target branches are both '{Branch}'.", job.Id, sourceBranch);
            return;
        }

        var pullRequestTitle = JobSummaryGenerator.BuildCommitSubject(job);
        var pullRequestBody = BuildPullRequestBody(job, sourceBranch, targetBranch);
        var pullRequestResult = await _versionControlService.CreatePullRequestAsync(
            workingDirectory,
            sourceBranch,
            targetBranch,
            pullRequestTitle,
            pullRequestBody,
            cancellationToken);

        if (!pullRequestResult.Success)
        {
            _logger.LogWarning("Failed to create pull request for job {JobId}: {Error}", job.Id, pullRequestResult.Error);
            return;
        }

        job.PullRequestNumber = pullRequestResult.PullRequestNumber;
        job.PullRequestUrl = pullRequestResult.PullRequestUrl;
        job.PullRequestCreatedAt = DateTime.UtcNow;
        _logger.LogInformation("Created pull request for job {JobId}: {PullRequestUrl}", job.Id, job.PullRequestUrl);
    }

    internal static async Task<(int ExitCode, string Output, string Error)> RunShellCommandAsync(
        string command, string workingDirectory, CancellationToken cancellationToken)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "/bin/bash",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // The command must reach bash as a single argument. Arguments is split by .NET's own
        // rules, which ignore single quotes, so "-c 'dotnet build'" arrived as "'dotnet" and
        // every multi-word command failed with a quoting error before it ran.
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add(command);
        RemoveVibeSwarmDatabaseSettings(process.StartInfo.Environment);

        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        // 5 minute timeout for build/test commands
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout — kill process
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return (-1, await outputTask, "Build verification timed out after 5 minutes.");
        }

        return (process.ExitCode, await outputTask, await errorTask);
    }

    /// <summary>
    /// VibeSwarm loads its own database settings into its process environment, and every child
    /// inherits them. A project's tests would then read VibeSwarm's provider and connection
    /// string instead of their own, so verification runs without them.
    /// </summary>
    internal static void RemoveVibeSwarmDatabaseSettings(IDictionary<string, string?> environment)
    {
        var inherited = environment.Keys
            .Where(name => string.Equals(name, "DATABASE_PROVIDER", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("ConnectionStrings__", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var name in inherited)
        {
            environment.Remove(name);
        }
    }

    private static string TruncateBuildOutput(string output)
    {
        const int maxLength = 50_000;
        if (output.Length <= maxLength) return output;
        return output[..(maxLength - 100)] + "\n\n... [output truncated] ...";
    }

    private static bool BranchExists(IReadOnlyList<GitBranchInfo> branches, string branchName)
    {
        return branches.Any(branch =>
            string.Equals(branch.Name, branchName, StringComparison.Ordinal) ||
            string.Equals(branch.ShortName, branchName, StringComparison.Ordinal));
    }

    private static bool ShouldProcessGitDelivery(Job job)
    {
        return job.Project?.AutoCommitMode != AutoCommitMode.Off
            || job.Project?.IdeasAutoCommit == true
            || ShouldCreatePullRequest(job);
    }

    private static bool ShouldCreatePullRequest(Job job)
    {
        return job.GitChangeDeliveryMode == GitChangeDeliveryMode.PullRequest
            && !string.IsNullOrWhiteSpace(GetEffectiveTargetBranch(job));
    }

    private static string? GetEffectiveTargetBranch(Job job)
    {
        return string.IsNullOrWhiteSpace(job.TargetBranch)
            ? string.IsNullOrWhiteSpace(job.Project?.DefaultTargetBranch) ? null : job.Project.DefaultTargetBranch.Trim()
            : job.TargetBranch.Trim();
    }

    private static string BuildPullRequestBody(Job job, string sourceBranch, string targetBranch)
    {
        var body = new StringBuilder();
        body.AppendLine("## VibeSwarm Job");
        body.AppendLine();
        body.AppendLine($"- Source branch: `{sourceBranch}`");
        body.AppendLine($"- Target branch: `{targetBranch}`");
        body.AppendLine($"- Job: `{job.Title ?? job.GoalPrompt}`");
        body.AppendLine();
        body.AppendLine("### Goal");
        body.AppendLine(job.GoalPrompt.Trim());

        if (!string.IsNullOrWhiteSpace(job.SessionSummary))
        {
            body.AppendLine();
            body.AppendLine("### Session Summary");
            body.AppendLine(job.SessionSummary.Trim());
        }

        return body.ToString().Trim();
    }

    private async Task<string> BuildCommitMessageAsync(Job job, string workingDirectory, CancellationToken cancellationToken)
    {
        var inferenceSummary = await TryGenerateInferenceCommitSummaryAsync(job, workingDirectory, cancellationToken);
        if (!string.IsNullOrWhiteSpace(inferenceSummary))
        {
            return inferenceSummary;
        }

        return JobSummaryGenerator.BuildCommitSubject(job);
    }

    private async Task<string?> TryGenerateInferenceCommitSummaryAsync(Job job, string workingDirectory, CancellationToken cancellationToken)
    {
        var project = job.Project;
        if (project?.CommitSummaryInferenceProviderId is not Guid inferenceProviderId)
        {
            return null;
        }

        using var scope = _scopeFactory.CreateScope();
        var inferenceProviderService = scope.ServiceProvider.GetRequiredService<IInferenceProviderService>();
        var inferenceService = scope.ServiceProvider.GetRequiredService<IInferenceService>();

        var provider = await inferenceProviderService.GetByIdAsync(inferenceProviderId, cancellationToken);
        if (provider == null || !provider.IsEnabled)
        {
            _logger.LogWarning(
                "Skipping inference commit summary for job {JobId} because provider {ProviderId} is unavailable.",
                job.Id,
                inferenceProviderId);
            return null;
        }

        InferenceModel? selectedModel;
        if (!string.IsNullOrWhiteSpace(project.CommitSummaryInferenceModelId))
        {
            selectedModel = provider.Models
                .Where(model => model.IsAvailable)
                .FirstOrDefault(model => string.Equals(model.ModelId, project.CommitSummaryInferenceModelId, StringComparison.Ordinal));

            if (selectedModel == null)
            {
                _logger.LogWarning(
                    "Skipping inference commit summary for job {JobId} because model {ModelId} is unavailable on provider {ProviderName}.",
                    job.Id,
                    project.CommitSummaryInferenceModelId,
                    provider.Name);
                return null;
            }
        }
        else
        {
            selectedModel = ResolveCommitSummaryModel(provider);
            if (selectedModel == null)
            {
                _logger.LogWarning(
                    "Skipping inference commit summary for job {JobId} because provider {ProviderName} has no default commit-summary model configured.",
                    job.Id,
                    provider.Name);
                return null;
            }
        }

        IReadOnlyList<string> changedFiles;
        try
        {
            changedFiles = await _versionControlService.GetChangedFilesAsync(workingDirectory, null, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect changed files for inference commit summary on job {JobId}", job.Id);
            changedFiles = [];
        }

        if (changedFiles.Count == 0 && !string.IsNullOrWhiteSpace(job.GitDiff))
        {
            changedFiles = JobSummaryGenerator.ParseGitDiff(job.GitDiff)
                .ChangedFiles
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        var response = await inferenceService.GenerateAsync(new InferenceRequest
        {
            TaskType = "commit-summary",
            Prompt = BuildCommitSummaryPrompt(job.GoalPrompt, changedFiles),
            SystemPrompt = "Write a single concise git commit subject line. Respond with only the subject, no quotes or bullets. Aim for 72 characters, and never exceed 96 characters.",
            Endpoint = provider.Endpoint,
            Model = selectedModel.ModelId,
            ProviderType = provider.ProviderType,
            Temperature = 0.2,
            MaxTokens = 60
        }, cancellationToken);

        if (!response.Success || string.IsNullOrWhiteSpace(response.Response))
        {
            _logger.LogWarning(
                "Inference commit summary generation failed for job {JobId} with provider {ProviderName}: {Error}",
                job.Id,
                provider.Name,
                response.Error ?? "No response");
            return null;
        }

        var commitSubject = JobSummaryGenerator.BuildCommitSubject(response.Response, null, job.GoalPrompt);
        _logger.LogInformation(
            "Generated inference commit summary for job {JobId} using {ProviderName}: {Summary}",
            job.Id,
            provider.Name,
            commitSubject);
        return commitSubject;
    }

    private static string BuildCommitSummaryPrompt(string goalPrompt, IReadOnlyList<string> changedFiles)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Goal prompt:");
        builder.AppendLine(goalPrompt.Trim());
        builder.AppendLine();
        builder.AppendLine("Changed files:");

        if (changedFiles.Count == 0)
        {
            builder.AppendLine("- No changed files detected");
        }
        else
        {
            foreach (var file in changedFiles.Take(50))
            {
                builder.Append("- ");
                builder.AppendLine(file);
            }
        }

        return builder.ToString().Trim();
    }

    private static InferenceModel? ResolveCommitSummaryModel(InferenceProvider provider)
    {
        return provider.Models
            .Where(model => model.IsAvailable && model.IsDefault && string.Equals(model.TaskType, "commit-summary", StringComparison.OrdinalIgnoreCase))
            .OrderBy(model => model.ModelId)
            .FirstOrDefault()
            ?? provider.Models
                .Where(model => model.IsAvailable && model.IsDefault && string.Equals(model.TaskType, "default", StringComparison.OrdinalIgnoreCase))
                .OrderBy(model => model.ModelId)
                .FirstOrDefault();
    }
}
