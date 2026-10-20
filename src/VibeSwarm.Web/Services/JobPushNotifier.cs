using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;

namespace VibeSwarm.Web.Services;

/// <summary>
/// Sends push notifications when a job finishes, fails or waits for input. <see cref="SignalRJobUpdateService"/>
/// calls it alongside the in-app events, and it works in the background so a slow or unreachable
/// push service never holds up a job.
/// </summary>
public class JobPushNotifier
{
	/// <summary>
	/// How long a question waits before the user is told about it while inference answers agents'
	/// questions, so a question inference answers doesn't page anyone.
	/// </summary>
	internal static readonly TimeSpan InferenceAnswerGrace = TimeSpan.FromSeconds(90);

	private const int MaxRememberedEvents = 500;

	private readonly IServiceScopeFactory _scopeFactory;
	private readonly ILogger<JobPushNotifier> _logger;
	private readonly TimeProvider _timeProvider;
	private readonly ConcurrentDictionary<string, DateTime> _sentEvents = new();

	public JobPushNotifier(IServiceScopeFactory scopeFactory, ILogger<JobPushNotifier> logger, TimeProvider? timeProvider = null)
	{
		_scopeFactory = scopeFactory;
		_logger = logger;
		_timeProvider = timeProvider ?? TimeProvider.System;
	}

	public void JobCompleted(Guid jobId) => RunInBackground(() => NotifyJobCompletedAsync(jobId));

	public void JobInteractionRequired(Guid jobId) => RunInBackground(() => NotifyInteractionRequiredAsync(jobId));

	/// <summary>
	/// Tells devices about a job that just ended, going by the status it was saved with: several
	/// services report the same ending, and a cancelled job (the user stopped it) is left alone.
	/// </summary>
	public async Task NotifyJobCompletedAsync(Guid jobId, CancellationToken cancellationToken = default)
	{
		using var scope = _scopeFactory.CreateScope();
		var job = await LoadAsync(scope, jobId, cancellationToken);
		var pushEvent = job?.Status switch
		{
			JobStatus.Completed => JobPushEvent.Completed,
			JobStatus.Failed or JobStatus.Stalled => JobPushEvent.Failed,
			_ => (JobPushEvent?)null
		};
		if (job == null || pushEvent == null || !TryClaim($"{jobId}:{job.Status}:{job.StartedAt?.Ticks}:{job.CompletedAt?.Ticks}"))
		{
			return;
		}

		await SendAsync(scope, pushEvent.Value, BuildMessage(job, pushEvent.Value), cancellationToken);
	}

	/// <summary>
	/// Tells devices a job is waiting on a question. While inference answers questions, waits a
	/// little first and sends only if the job still waits on the same one.
	/// </summary>
	public async Task NotifyInteractionRequiredAsync(Guid jobId, CancellationToken cancellationToken = default)
	{
		using var scope = _scopeFactory.CreateScope();
		var job = await LoadAsync(scope, jobId, cancellationToken);
		if (job?.Status != JobStatus.Paused || job.InteractionRequestedAt == null)
		{
			return;
		}

		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
		var inferenceAnswers = await dbContext.AppSettings.AsNoTracking().OrderBy(s => s.Id)
			.Select(s => (bool?)s.InferenceAnswersAgentQuestions).FirstOrDefaultAsync(cancellationToken) ?? true;
		if (inferenceAnswers)
		{
			await Task.Delay(InferenceAnswerGrace, _timeProvider, cancellationToken);
			var current = await LoadAsync(scope, jobId, cancellationToken);
			if (current?.Status != JobStatus.Paused || current.InteractionRequestedAt != job.InteractionRequestedAt)
			{
				return;
			}
			job = current;
		}

		if (!TryClaim($"{jobId}:input:{job.InteractionRequestedAt!.Value.Ticks}"))
		{
			return;
		}

		await SendAsync(scope, JobPushEvent.InputRequired, BuildMessage(job, JobPushEvent.InputRequired), cancellationToken);
	}

	internal static PushMessage BuildMessage(JobPushInfo job, JobPushEvent pushEvent)
	{
		var project = string.IsNullOrWhiteSpace(job.ProjectName) ? "VibeSwarm" : job.ProjectName.Trim();
		var title = string.IsNullOrWhiteSpace(job.Title) ? FirstLine(job.GoalPrompt) : job.Title.Trim();
		title = PushNotificationService.Truncate(title, 120);

		var (heading, detail) = pushEvent switch
		{
			JobPushEvent.Completed => ($"{project}: job finished", null),
			JobPushEvent.Failed when job.Status == JobStatus.Stalled => ($"{project}: job stalled", job.ErrorMessage),
			JobPushEvent.Failed => ($"{project}: job failed", job.ErrorMessage),
			_ => ($"{project}: needs your input", job.PendingInteractionPrompt)
		};

		var body = string.IsNullOrWhiteSpace(detail)
			? title
			: $"{title}\n{PushNotificationService.Truncate(FirstLine(detail), 160)}";

		return new PushMessage(heading, body, $"/jobs/view/{job.Id}", $"job-{job.Id}", Urgent: pushEvent != JobPushEvent.Completed);
	}

	private async Task SendAsync(IServiceScope scope, JobPushEvent pushEvent, PushMessage message, CancellationToken cancellationToken)
	{
		var delivered = await scope.ServiceProvider.GetRequiredService<PushNotificationService>()
			.SendAsync(pushEvent, message, cancellationToken);
		if (delivered > 0)
		{
			_logger.LogDebug("Sent {Event} push notification to {Count} device(s)", pushEvent, delivered);
		}
	}

	private static Task<JobPushInfo?> LoadAsync(IServiceScope scope, Guid jobId, CancellationToken cancellationToken)
		=> scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>().Jobs.AsNoTracking()
			.Where(j => j.Id == jobId)
			.Select(j => new JobPushInfo(j.Id, j.Status, j.Title, j.GoalPrompt, j.Project != null ? j.Project.Name : null,
				j.ErrorMessage, j.PendingInteractionPrompt, j.StartedAt, j.CompletedAt, j.InteractionRequestedAt))
			.FirstOrDefaultAsync(cancellationToken);

	/// <summary>True the first time an event is seen, so each one is sent once.</summary>
	private bool TryClaim(string eventKey)
	{
		var now = _timeProvider.GetUtcNow().UtcDateTime;
		if (_sentEvents.Count > MaxRememberedEvents)
		{
			foreach (var stale in _sentEvents.Where(e => e.Value < now.AddDays(-1)).Select(e => e.Key).ToList())
			{
				_sentEvents.TryRemove(stale, out _);
			}
		}
		return _sentEvents.TryAdd(eventKey, now);
	}

	private void RunInBackground(Func<Task> work)
	{
		_ = Task.Run(async () =>
		{
			try
			{
				await work();
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Could not send a job push notification");
			}
		});
	}

	private static string FirstLine(string? text)
	{
		var trimmed = text?.Trim() ?? string.Empty;
		var newline = trimmed.IndexOfAny(['\r', '\n']);
		return newline < 0 ? trimmed : trimmed[..newline].TrimEnd();
	}
}

/// <summary>The parts of a job a notification needs.</summary>
internal sealed record JobPushInfo(Guid Id, JobStatus Status, string? Title, string GoalPrompt, string? ProjectName,
	string? ErrorMessage, string? PendingInteractionPrompt, DateTime? StartedAt, DateTime? CompletedAt, DateTime? InteractionRequestedAt);
