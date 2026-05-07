using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Services;

public static class JobRecoveryHelper
{
	public const int MaxRecoveryConsoleOutputLength = 32000;

	public static bool IsResumeCandidate(Job job)
	{
		return job.ResumeFromStatus is JobStatus.Started or JobStatus.Processing;
	}

	public static bool ShouldAttemptSessionResume(Job job)
	{
		return IsResumeCandidate(job)
			&& !job.ForceFreshSession
			&& !string.IsNullOrWhiteSpace(job.SessionId);
	}

	public static void CaptureRecoveryState(
		Job job,
		JobStatus resumeFromStatus,
		string? recoveryPrompt,
		string? sessionId,
		string? consoleOutput)
	{
		job.ResumeFromStatus = resumeFromStatus;
		job.RecoveryCheckpointAt = DateTime.UtcNow;

		if (!string.IsNullOrWhiteSpace(recoveryPrompt))
		{
			job.RecoveryPrompt = recoveryPrompt;
		}

		if (!string.IsNullOrWhiteSpace(sessionId))
		{
			job.SessionId = sessionId;
		}

		if (!string.IsNullOrWhiteSpace(consoleOutput))
		{
			job.ConsoleOutput = TrimTail(consoleOutput, MaxRecoveryConsoleOutputLength);
		}
	}

	public static void RecordResumeAttempt(Job job)
	{
		job.ResumeAttemptCount++;
		job.LastResumeAttemptAt = DateTime.UtcNow;
		job.LastResumeFailureReason = null;
	}

	public static void RecordResumeFailure(Job job, string? reason)
	{
		job.LastResumeAttemptAt = DateTime.UtcNow;
		job.LastResumeFailureReason = TrimTail(reason, 1000);
		job.ForceFreshSession = true;
		job.SessionId = null;
	}

	public static void ClearRecoveryState(Job job, bool clearSessionId = false)
	{
		job.ResumeFromStatus = null;
		job.RecoveryCheckpointAt = null;
		job.RecoveryPrompt = null;
		job.ResumeAttemptCount = 0;
		job.LastResumeAttemptAt = null;
		job.LastResumeFailureReason = null;
		job.ForceFreshSession = false;

		if (clearSessionId)
		{
			job.SessionId = null;
		}
	}

	public static string? TrimTail(string? value, int maxLength)
	{
		if (string.IsNullOrWhiteSpace(value) || value.Length <= maxLength)
		{
			return value;
		}

		return value[^maxLength..];
	}
}