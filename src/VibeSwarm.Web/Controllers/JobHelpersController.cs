using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.Validation;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Web.Controllers;

/// <summary>
/// Answers a running job's <c>vibeswarm-ask</c> command. The agent has no login, so the run's
/// token stands in for one: it is made when the run starts and dies with it. Replies are plain
/// text, because the agent reads them in a shell.
/// </summary>
[ApiController]
[Route("api/jobs/{id:guid}/helpers")]
[AllowAnonymous]
public class JobHelpersController : ControllerBase
{
	private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(1);

	private readonly JobHelperSessions _sessions;
	private readonly IReadOnlyConsultService _consult;
	private readonly IServiceScopeFactory _scopeFactory;
	private readonly IJobUpdateService? _jobUpdates;
	private readonly ILogger<JobHelpersController> _logger;

	public JobHelpersController(
		JobHelperSessions sessions,
		IReadOnlyConsultService consult,
		IServiceScopeFactory scopeFactory,
		ILogger<JobHelpersController> logger,
		IJobUpdateService? jobUpdates = null)
	{
		_sessions = sessions;
		_consult = consult;
		_scopeFactory = scopeFactory;
		_logger = logger;
		_jobUpdates = jobUpdates;
	}

	[HttpPost("ask")]
	public async Task<IActionResult> Ask(Guid id, CancellationToken cancellationToken)
	{
		var session = _sessions.Find(id, Request.Headers[JobHelperCommand.TokenHeader].ToString());
		if (session == null)
		{
			return Reply(StatusCodes.Status401Unauthorized, "This job has no helpers to ask right now.");
		}

		var helperName = Request.Headers[JobHelperCommand.HelperHeader].ToString();
		var helper = session.Find(helperName);
		if (helper == null)
		{
			return Reply(StatusCodes.Status404NotFound,
				$"There is no helper called '{helperName}'. Ask one of: {string.Join(", ", session.Helpers.Select(candidate => candidate.Name))}.");
		}

		var question = await ReadQuestionAsync(cancellationToken);
		if (question == null)
		{
			return Reply(StatusCodes.Status413PayloadTooLarge,
				$"The question is longer than {ValidationLimits.JobHelperQuestionMaxLength:N0} characters. Shorten it and ask again.");
		}

		if (string.IsNullOrWhiteSpace(question))
		{
			return Reply(StatusCodes.Status400BadRequest, "The question is empty. Pass it after the helper's name, or pipe it in.");
		}

		if (!await session.Gate.WaitAsync(0, cancellationToken))
		{
			return Reply(StatusCodes.Status409Conflict, "Another question to a helper is still being answered. Wait for it, then ask again.");
		}

		try
		{
			if (!session.TryCountQuestion(ValidationLimits.JobHelperQuestionsPerRun))
			{
				return Reply(StatusCodes.Status429TooManyRequests,
					$"This run has asked its {ValidationLimits.JobHelperQuestionsPerRun} questions. Carry on with what you know.");
			}

			using var asking = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Ended.Token);
			var activity = $"Asking {helper.Name} ({helper.ProfileName})...";
			var heartbeat = KeepAliveAsync(id, activity, asking.Token);
			try
			{
				var result = await _consult.ConsultAsync(new ConsultRequest
				{
					Target = helper.Target,
					Prompt = question.Trim(),
					SystemPrompt = BuildSystemPrompt(helper),
					WorkingDirectory = helper.ReadsRepository ? session.WorkingDirectory : null,
					JobId = id,
					Title = $"{helper.Name} helper",
					TaskType = "default",
					Timeout = JobHelperCommand.AnswerTimeout,
				}, asking.Token);

				if (result.Success && !string.IsNullOrWhiteSpace(result.Text))
				{
					return Reply(StatusCodes.Status200OK, result.Text.Trim());
				}

				var reason = result.Error ?? "it gave no answer";
				return Reply(StatusCodes.Status502BadGateway, $"{helper.Name} couldn't answer: {reason}. Carry on without it.");
			}
			catch (OperationCanceledException) when (session.Ended.IsCancellationRequested)
			{
				return Reply(StatusCodes.Status410Gone, "The job's run ended before the helper answered.");
			}
			finally
			{
				await asking.CancelAsync();
				await heartbeat;
			}
		}
		finally
		{
			session.Gate.Release();
		}
	}

	private static string BuildSystemPrompt(JobHelper helper)
	{
		var reads = helper.ReadsRepository
			? "You can read the repository in your working directory, but never change it."
			: "You see only the question, not the repository.";
		return $"""
			You are the {helper.Name} helper for a coding agent that is part-way through a job. {helper.Purpose}
			{reads}
			Answer the question directly and concisely, in plain text, with what the agent needs to act on it.
			""";
	}

	/// <summary>Reads the question, or returns null when it is longer than allowed.</summary>
	private async Task<string?> ReadQuestionAsync(CancellationToken cancellationToken)
	{
		using var reader = new StreamReader(Request.Body, Encoding.UTF8);
		var buffer = new char[ValidationLimits.JobHelperQuestionMaxLength + 1];
		var read = 0;
		while (read < buffer.Length)
		{
			var count = await reader.ReadAsync(buffer.AsMemory(read), cancellationToken);
			if (count == 0)
			{
				break;
			}

			read += count;
		}

		return read > ValidationLimits.JobHelperQuestionMaxLength ? null : new string(buffer, 0, read);
	}

	/// <summary>
	/// Tells the watchdog the job is alive while its agent waits on an answer, which can take
	/// longer than the stall threshold, and shows what it is waiting for.
	/// </summary>
	private async Task KeepAliveAsync(Guid jobId, string activity, CancellationToken cancellationToken)
	{
		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				using (var scope = _scopeFactory.CreateScope())
				{
					var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
					var now = DateTime.UtcNow;
					await dbContext.Jobs
						.Where(job => job.Id == jobId)
						.ExecuteUpdateAsync(setters => setters
							.SetProperty(job => job.LastHeartbeatAt, now)
							.SetProperty(job => job.LastActivityAt, now)
							.SetProperty(job => job.CurrentActivity, activity), CancellationToken.None);
				}

				if (_jobUpdates != null)
				{
					await _jobUpdates.NotifyJobActivity(jobId, activity, DateTime.UtcNow);
				}

				await Task.Delay(HeartbeatInterval, cancellationToken);
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Could not keep job {JobId} alive while it asked a helper", jobId);
		}
	}

	private ContentResult Reply(int statusCode, string text)
		=> new() { StatusCode = statusCode, Content = text, ContentType = "text/plain; charset=utf-8" };
}
