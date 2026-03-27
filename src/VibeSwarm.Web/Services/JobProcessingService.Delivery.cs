using Microsoft.Extensions.Logging;
using System.Text;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.VersionControl.Models;
using VibeSwarm.Shared;

namespace VibeSwarm.Web.Services;

public partial class JobProcessingService
{
    /// <summary>
    /// Performs auto-commit (and optionally push) based on project settings.
    /// </summary>
    private async Task PerformAutoCommitAsync(Job job, string workingDirectory, CancellationToken cancellationToken)
    {
        try
        {
            // Remove agent artifacts (task files, screenshots, etc.) before staging
            CleanupAgentArtifacts(job.Id, workingDirectory);

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
                            var pushResult = await _versionControlService.PushAsync(workingDirectory, cancellationToken: cancellationToken);
                            if (pushResult.Success)
                            {
                                _logger.LogInformation("Auto-pushed agent-committed changes for job {JobId}", job.Id);
                            }
                            else
                            {
                                _logger.LogWarning("Auto-push failed for job {JobId}: {Error}. Changes were committed but not pushed.",
                                    job.Id, pushResult.Error);
                            }
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

            var commitMessage = job.SessionSummary ?? $"{AppConstants.AppName}: {job.Title ?? "Job completed"}";

            _logger.LogInformation("Auto-committing changes for job {JobId} with mode {Mode}",
                job.Id, effectiveCommitMode);

            var commitResult = await _versionControlService.CommitAllChangesAsync(
                workingDirectory,
                commitMessage,
                cancellationToken);

            if (commitResult.Success)
            {
                job.GitCommitHash = commitResult.CommitHash;
                _logger.LogInformation("Auto-committed changes for job {JobId}: {CommitHash}",
                    job.Id, commitResult.CommitHash?[..Math.Min(8, commitResult.CommitHash?.Length ?? 0)]);

                // Push if configured
                if (effectiveCommitMode == AutoCommitMode.CommitAndPush)
                {
                    var pushResult = await _versionControlService.PushAsync(workingDirectory, cancellationToken: cancellationToken);
                    if (pushResult.Success)
                    {
                        _logger.LogInformation("Auto-pushed changes for job {JobId}", job.Id);
                    }
                    else
                    {
                        // Push failed, but commit succeeded - log warning but don't fail the job
                        _logger.LogWarning("Auto-push failed for job {JobId}: {Error}. Changes were committed but not pushed.",
                            job.Id, pushResult.Error);
                    }
                }
            }
            else
            {
                _logger.LogWarning("Auto-commit failed for job {JobId}: {Error}", job.Id, commitResult.Error);
            }
        }
        catch (Exception ex)
        {
            // Auto-commit failures should not fail the job
            _logger.LogWarning(ex, "Error during auto-commit for job {JobId}", job.Id);
        }
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

            // Run build command
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

        var pullRequestTitle = job.SessionSummary ?? $"{AppConstants.AppName}: {job.Title ?? "Job completed"}";
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

    private static async Task<(int ExitCode, string Output, string Error)> RunShellCommandAsync(
        string command, string workingDirectory, CancellationToken cancellationToken)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = $"-c {EscapeShellArgument(command)}",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

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

    private static string EscapeShellArgument(string argument)
    {
        // Wrap in single quotes, escaping any embedded single quotes
        return "'" + argument.Replace("'", "'\\''") + "'";
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

    /// <summary>
    /// Deletes known agent artifact files from the working directory so they are not
    /// included in auto-commits. Artifacts are non-code files that CLI agents create
    /// for their own internal tracking (task lists, screenshots, etc.).
    /// </summary>
    private void CleanupAgentArtifacts(Guid jobId, string workingDirectory)
    {
        foreach (var pattern in ProjectMemoryService.ArtifactCleanupPatterns)
        {
            try
            {
                var fullPath = Path.Combine(workingDirectory, pattern);
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                    _logger.LogInformation("Cleaned up agent artifact {Artifact} for job {JobId}", pattern, jobId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to clean up agent artifact {Artifact} for job {JobId}", pattern, jobId);
            }
        }

        // Remove empty tasks/ directory if all artifact files were deleted
        try
        {
            var tasksDir = Path.Combine(workingDirectory, "tasks");
            if (Directory.Exists(tasksDir) && !Directory.EnumerateFileSystemEntries(tasksDir).Any())
            {
                Directory.Delete(tasksDir);
                _logger.LogDebug("Removed empty tasks/ directory for job {JobId}", jobId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to remove empty tasks/ directory for job {JobId}", jobId);
        }
    }
}
