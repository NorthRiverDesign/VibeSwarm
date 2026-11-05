using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Services;

public partial class JobProcessingService
{
	/// <summary>
	/// Checks the agent's finished work before delivery: the project's build and tests first, then a
	/// second model reading the diff. Returns what to send back to the agent, or null when the work
	/// should be delivered. Review never holds work back: whenever it can't run, it records why and
	/// returns null.
	/// </summary>
	internal async Task<string?> TryGetReviewFeedbackAsync(
		Job job,
		string workingDirectory,
		string? baseCommit,
		string? sessionId,
		bool canSkipFinalBuild,
		CancellationToken cancellationToken)
	{
		var project = job.Project;
		if (project is not { ReviewEnabled: true }
			|| job.ReviewStatus is JobReviewStatus.Approved or JobReviewStatus.Unresolved or JobReviewStatus.Skipped
			|| !string.IsNullOrWhiteSpace(job.MergeFromBranch)
			|| LocalEnvironmentSetup.IsSetupJob(job))
		{
			return null;
		}

		if (job.ReviewRounds >= JobReview.MaxRounds)
		{
			await SaveReviewAsync(job, JobReviewStatus.Unresolved, job.ReviewNotes, cancellationToken);
			return null;
		}

		// Findings go back to the agent in its own session; without one there is nobody to fix them.
		if (string.IsNullOrEmpty(sessionId))
		{
			await SaveReviewAsync(job, JobReviewStatus.Skipped, "The agent's session can't be continued", cancellationToken);
			return null;
		}

		try
		{
			if (project.BuildVerificationEnabled && !string.IsNullOrWhiteSpace(project.BuildCommand))
			{
				await NotifyReviewActivityAsync(job.Id, "Checking the build and tests before review...");
				if (!await VerifyBuildAsync(job, workingDirectory, cancellationToken))
				{
					cancellationToken.ThrowIfCancellationRequested();
					job.ReviewRounds++;
					await SaveReviewAsync(job, JobReviewStatus.None, "The build or tests failed; sent back to the agent", cancellationToken);
					return ReviewPrompts.BuildBuildFailurePrompt(job.BuildOutput, job.ReviewRounds);
				}

				if (canSkipFinalBuild && _workSnapshots != null)
				{
					job.BuildCheckedTreeHash = await _workSnapshots.HashWorkingTreeAsync(workingDirectory, cancellationToken);
				}
			}

			var diff = await _versionControlService.GetWorkingDirectoryDiffAsync(workingDirectory, baseCommit, cancellationToken);
			if (string.IsNullOrWhiteSpace(diff))
			{
				await SaveReviewAsync(job, JobReviewStatus.Skipped, "No changes to review", cancellationToken);
				return null;
			}

			using var scope = _scopeFactory.CreateScope();
			var consult = scope.ServiceProvider.GetService<IReadOnlyConsultService>();
			if (consult == null)
			{
				return null;
			}

			await NotifyReviewActivityAsync(job.Id, "A second model is reviewing the work...");
			var target = new ConsultTarget(project.ReviewProviderId, project.ReviewInferenceProviderId, project.ReviewModelId, project.ReviewReasoningEffort);
			var result = await consult.ConsultAsync(new ConsultRequest
			{
				Target = target,
				Prompt = ReviewPrompts.BuildReviewPrompt(job.GoalPrompt, job.PlanningOutput, diff, canOpenFiles: target.ProviderId.HasValue),
				SystemPrompt = ReviewPrompts.SystemPrompt,
				WorkingDirectory = workingDirectory,
				JobId = job.Id,
				Title = string.IsNullOrWhiteSpace(job.Title) ? "Review" : $"Review: {job.Title}",
				TaskType = ReviewPrompts.TaskType
			}, cancellationToken);

			job.ReviewProviderName = result.ProviderName ?? job.ReviewProviderName;
			if (result.CostUsd is decimal cost)
			{
				job.ReviewCostUsd = (job.ReviewCostUsd ?? 0) + cost;
			}

			if (!result.Success)
			{
				await SaveReviewAsync(job, JobReviewStatus.Skipped, result.Error ?? "The reviewer gave no answer", cancellationToken);
				return null;
			}

			var verdict = ReviewPrompts.ParseVerdict(result.Text);
			if (verdict == null)
			{
				await SaveReviewAsync(job, JobReviewStatus.Skipped, "The reviewer gave no verdict", cancellationToken);
				return null;
			}

			if (verdict.Approved)
			{
				await SaveReviewAsync(job, JobReviewStatus.Approved, null, cancellationToken);
				return null;
			}

			job.ReviewRounds++;
			await SaveReviewAsync(job, JobReviewStatus.None, verdict.Issues, cancellationToken);
			_logger.LogInformation("Review of job {JobId} found problems; sending round {Round} back to the agent", job.Id, job.ReviewRounds);
			return ReviewPrompts.BuildFixPrompt(verdict.Issues!, job.ReviewProviderName, job.ReviewRounds);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Review of job {JobId} failed; delivering the work without it", job.Id);
			await SaveReviewAsync(job, JobReviewStatus.Skipped, $"Review failed: {ex.Message}", CancellationToken.None);
			return null;
		}
	}

	private async Task SaveReviewAsync(Job job, JobReviewStatus status, string? notes, CancellationToken cancellationToken)
	{
		job.ReviewStatus = status;
		job.ReviewNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Length > 8000 ? notes[..8000] : notes;

		using var scope = _scopeFactory.CreateScope();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
		await dbContext.Jobs
			.Where(j => j.Id == job.Id)
			.ExecuteUpdateAsync(setters => setters
				.SetProperty(j => j.ReviewStatus, job.ReviewStatus)
				.SetProperty(j => j.ReviewRounds, job.ReviewRounds)
				.SetProperty(j => j.ReviewNotes, job.ReviewNotes)
				.SetProperty(j => j.ReviewProviderName, job.ReviewProviderName)
				.SetProperty(j => j.ReviewCostUsd, job.ReviewCostUsd)
				.SetProperty(j => j.BuildCheckedTreeHash, job.BuildCheckedTreeHash)
				.SetProperty(j => j.BuildVerified, job.BuildVerified)
				.SetProperty(j => j.BuildOutput, job.BuildOutput),
				CancellationToken.None);
	}

	private async Task NotifyReviewActivityAsync(Guid jobId, string activity)
	{
		try
		{
			await NotifyJobActivityAsync(jobId, activity, DateTime.UtcNow);
		}
		catch (Exception ex)
		{
			_logger.LogDebug(ex, "Could not report review activity for job {JobId}", jobId);
		}
	}

	/// <summary>
	/// Delivery's build check, skipped when the review already built this exact tree.
	/// </summary>
	private async Task<bool> VerifyBuildUnlessAlreadyCheckedAsync(Job job, string workingDirectory, CancellationToken cancellationToken)
	{
		if (job.BuildVerified == true
			&& !string.IsNullOrWhiteSpace(job.BuildCheckedTreeHash)
			&& _workSnapshots != null)
		{
			var tree = await _workSnapshots.HashWorkingTreeAsync(workingDirectory, cancellationToken);
			if (string.Equals(tree, job.BuildCheckedTreeHash, StringComparison.OrdinalIgnoreCase))
			{
				_logger.LogInformation("Job {JobId}'s build already passed on this exact tree during review; not building again", job.Id);
				return true;
			}
		}

		return await VerifyBuildAsync(job, workingDirectory, cancellationToken);
	}
}
