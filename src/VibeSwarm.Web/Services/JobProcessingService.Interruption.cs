using Microsoft.Extensions.Logging;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Services;

public partial class JobProcessingService
{
	internal const string InterruptedByShutdown = "Interrupted when VibeSwarm stopped.";
	internal const string InterruptedUnexpectedly = "Interrupted when VibeSwarm stopped unexpectedly (a crash or power loss).";

	/// <summary>
	/// Leaves a job that stopped mid-run (a restart, crash or power loss) paused and resumable.
	/// Its work is saved as the run's snapshot and on a recovery branch, its agent session and
	/// prompt are kept for the recovery flow, and it waits as Stalled, holding its project's
	/// queue, until it is resumed from the UI. Nothing is restarted on its own.
	/// </summary>
	internal async Task PauseInterruptedJobAsync(
		Job job,
		string? workingDirectory,
		VibeSwarmDbContext dbContext,
		string reason,
		string? activePrompt,
		string? consoleOutput,
		CancellationToken cancellationToken)
	{
		// A job that was only claimed has done nothing yet; it just goes back in the queue.
		if (job.Status == JobStatus.Pending)
		{
			JobStateMachine.TryTransition(job, JobStatus.New, reason);
			ClearWorkerFields(job);
			return;
		}

		if (!string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory))
		{
			try
			{
				RemoveStaleGitIndexLock(workingDirectory, GetBootTimeUtc());
				if (await _versionControlService.IsGitRepositoryAsync(workingDirectory, cancellationToken))
				{
					job.WorkSnapshotCommit = await SaveRunWorkSnapshotAsync(job, workingDirectory, job.GitCommitBefore, cancellationToken)
						?? job.WorkSnapshotCommit;
					await PreserveWorkingTreeBeforeBranchPreparationAsync(
						job,
						workingDirectory,
						dbContext,
						captureJobDiff: true,
						reason: "Preserved local changes when the job was interrupted.",
						cancellationToken: cancellationToken);
				}
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				_logger.LogWarning(ex, "Could not save all the work of interrupted job {JobId}", job.Id);
			}
		}

		JobRecoveryHelper.CaptureRecoveryState(
			job,
			job.Status == JobStatus.Planning ? JobStatus.Planning : JobStatus.Processing,
			activePrompt ?? job.RecoveryPrompt ?? job.GoalPrompt,
			job.SessionId,
			consoleOutput ?? job.ConsoleOutput);
		JobStateMachine.TryTransition(job, JobStatus.Stalled, reason);
		job.ErrorMessage = $"{reason} Its work so far is saved. Resume the job to continue where it left off.";
		ClearWorkerFields(job);

		_logger.LogWarning("Job {JobId} was interrupted and is paused until it is resumed: {Reason}", job.Id, reason);
	}

	private static void ClearWorkerFields(Job job)
	{
		job.WorkerInstanceId = null;
		job.LastHeartbeatAt = null;
		job.ProcessId = null;
		job.CurrentActivity = null;
	}

	/// <summary>
	/// A power loss in the middle of a git command leaves <c>.git/index.lock</c> behind, and every
	/// later git command in that checkout fails until it is gone. A lock older than the last boot
	/// cannot belong to a running process.
	/// </summary>
	internal static bool RemoveStaleGitIndexLock(string workingDirectory, DateTime? bootTimeUtc)
	{
		var lockFile = Path.Combine(workingDirectory, ".git", "index.lock");
		if (bootTimeUtc == null || !File.Exists(lockFile) || File.GetLastWriteTimeUtc(lockFile) >= bootTimeUtc.Value)
		{
			return false;
		}

		File.Delete(lockFile);
		return true;
	}

	private static DateTime? GetBootTimeUtc()
	{
		try
		{
			var uptime = File.ReadAllText("/proc/uptime").Split(' ')[0];
			return double.TryParse(uptime, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
				? DateTime.UtcNow - TimeSpan.FromSeconds(seconds)
				: null;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}
}
