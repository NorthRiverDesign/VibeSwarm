using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Inference;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.Utilities;

namespace VibeSwarm.Web.Services;

/// <summary>
/// A question an agent asked, with what the answerer needs to reply to it.
/// <paramref name="RunFinished"/> marks a question the agent ended its run with, which may only
/// offer optional extra work.
/// </summary>
public sealed record AgentQuestion(
	string GoalPrompt,
	string? ProjectName,
	string Question,
	InteractionDetector.InteractionType Type,
	IReadOnlyList<string>? Choices,
	IReadOnlyList<string> RecentOutput,
	bool RunFinished = false);

/// <summary>
/// An inference model's reply to an agent's question, worded for the agent.
/// </summary>
public sealed record AgentQuestionAnswer(string Text, string ProviderName, string ModelId);

/// <summary>
/// Answers an agent's question with inference, so an unattended job carries on instead of waiting
/// for the user. Enabled inference providers are tried in priority order, each with its model for
/// <see cref="TaskType"/> or else its default model, so an offline provider falls through to the
/// next. Returns null, leaving the question for the user, when the setting is off, no provider
/// answers, the question asks for credentials, or the model judges that only the user can decide.
/// </summary>
public sealed partial class AgentQuestionAnswerer
{
	/// <summary>The inference task type a model can be assigned to for answering agents.</summary>
	public const string TaskType = InferenceTaskModels.AgentAnswer;

	/// <summary>
	/// Answers given in one job before further questions wait for the user, so an agent that keeps
	/// asking can't loop on inference.
	/// </summary>
	public const int MaxAnswersPerJob = 3;

	/// <summary>The reply that tells VibeSwarm to leave the question for the user.</summary>
	internal const string NeedsUserReply = "NEEDS_USER";

	/// <summary>
	/// The reply to a closing question that tells VibeSwarm the goal is met, so the job can end.
	/// </summary>
	internal const string NoFollowUpReply = "NO_FOLLOW_UP";

	// A closing paragraph longer than this is a report that happens to contain a question mark.
	private const int MaxClosingQuestionLength = 600;
	private const int MaxFinalMessageLength = 4000;

	private const int MaxRecentOutputLines = 40;
	private const int MaxGoalLength = 4000;

	internal const string SystemPrompt =
		"You answer a coding agent's question on behalf of the project's owner, who is away. The agent is "
		+ "working unattended on the goal below, and its run stopped when it asked something. Reply with the "
		+ "answer only, in one to three sentences, addressed to the agent. Choose what best completes the goal "
		+ "safely; when the agent recommended an option, agree with it unless it conflicts with the goal. Tell it "
		+ "to proceed and to make any further decisions itself. Never ask a question back and never invent "
		+ "credentials, keys or account details. If answering needs a secret, a payment, deleting real data, or a "
		+ "decision only the owner can make, reply with exactly " + NeedsUserReply + ".";

	internal const string RunFinishedSystemPromptAddition =
		" The agent's run has ended and its question came at the end of its final message. If the goal is "
		+ "already met and the question only offers optional extras or invites feedback, reply with exactly "
		+ NoFollowUpReply + ". Answer it only when doing so completes or clearly improves the goal.";

	internal const string FollowUpSystemPrompt =
		"You write the follow-up message a project's owner sends to a coding agent whose run has ended. Read the "
		+ "goal, how the run ended and the agent's final message. If the agent asked something, answer it, agreeing "
		+ "with an option it recommended unless that conflicts with the goal. If the run failed or timed out, tell it "
		+ "how to recover and finish the goal; otherwise give it the most useful next step toward the goal. When the "
		+ "owner wrote notes, turn them into the message and keep their intent. Reply with the message only, in one "
		+ "to three sentences, addressed to the agent. Never ask a question back and never invent credentials, keys "
		+ "or account details. If the message needs a secret, a payment, deleting real data, or a decision only the "
		+ "owner can make, reply with exactly " + NeedsUserReply + ".";

	private const int MaxErrorLength = 1000;
	private const int MaxNotesLength = 2000;

	private readonly VibeSwarmDbContext _db;
	private readonly IInferenceService _inference;
	private readonly ILogger<AgentQuestionAnswerer> _logger;

	public AgentQuestionAnswerer(VibeSwarmDbContext db, IInferenceService inference, ILogger<AgentQuestionAnswerer> logger)
	{
		_db = db;
		_inference = inference;
		_logger = logger;
	}

	public async Task<AgentQuestionAnswer?> TryAnswerAsync(AgentQuestion question, CancellationToken cancellationToken = default)
	{
		if (question.Type == InteractionDetector.InteractionType.Authentication)
		{
			return null;
		}

		var enabled = await _db.AppSettings
			.AsNoTracking()
			.OrderBy(s => s.Id)
			.Select(s => (bool?)s.InferenceAnswersAgentQuestions)
			.FirstOrDefaultAsync(cancellationToken);
		if (enabled == false)
		{
			return null;
		}

		var reply = await AskAsync(
			BuildPrompt(question),
			question.RunFinished ? SystemPrompt + RunFinishedSystemPromptAddition : SystemPrompt,
			cancellationToken);
		if (reply == null)
		{
			return null;
		}

		var answer = CleanAnswer(reply.Text);
		if (answer == null)
		{
			// The model judged that only the user can answer, or that nothing is left to do;
			// another model won't know better.
			_logger.LogInformation(
				"Inference model {ModelId} on {ProviderName} declined to answer the agent's question ({Reply}): {Question}",
				reply.ModelId,
				reply.ProviderName,
				reply.Text.Contains(NoFollowUpReply, StringComparison.OrdinalIgnoreCase) ? "goal already met" : "left for the user",
				question.Question);
			return null;
		}

		return new AgentQuestionAnswer(answer, reply.ProviderName, reply.ModelId);
	}

	/// <summary>
	/// Writes the follow-up message for a job whose run has ended, for the user to confirm before
	/// it is sent. The user asked for it, so unlike <see cref="TryAnswerAsync"/> it ignores the
	/// setting for answering unattended agents. <paramref name="notes"/> is what the user had typed
	/// in the follow-up box, which the model turns into the message.
	/// </summary>
	public async Task<FollowUpDraft> DraftFollowUpAsync(Guid jobId, string? notes, CancellationToken cancellationToken = default)
	{
		var job = await _db.Jobs
			.AsNoTracking()
			.Include(j => j.Project)
			.FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);
		if (job == null)
		{
			return new FollowUpDraft { Error = "Job not found." };
		}

		if (job.Status is not (JobStatus.Completed or JobStatus.Stalled or JobStatus.Failed))
		{
			return new FollowUpDraft { Error = "The job is still running. Draft a reply once this run ends." };
		}

		var finalMessage = await _db.JobMessages
			.AsNoTracking()
			.Where(m => m.JobId == jobId && m.Role == MessageRole.Assistant && m.Content != string.Empty)
			.OrderByDescending(m => m.CreatedAt)
			.Select(m => m.Content)
			.FirstOrDefaultAsync(cancellationToken);

		var reply = await AskAsync(
			BuildFollowUpPrompt(job, finalMessage ?? job.SessionSummary, notes),
			FollowUpSystemPrompt,
			cancellationToken);
		if (reply == null)
		{
			return new FollowUpDraft { Error = "No inference provider replied. Check that one is enabled and online on the Inference page." };
		}

		// The request was spent either way, so a declined draft still reports what it used.
		var text = CleanAnswer(reply.Text);
		return new FollowUpDraft
		{
			Text = text,
			Error = text == null ? "Inference left this reply to you: it needs a decision only you can make." : null,
			ProviderName = reply.ProviderName,
			ProviderType = reply.ProviderType,
			ModelId = reply.ModelId,
			PromptTokens = reply.Response.PromptTokens,
			CompletionTokens = reply.Response.CompletionTokens,
			DurationMs = reply.DurationMs,
			CostUsd = reply.Response.CostUsd
		};
	}

	private sealed record ModelReply(
		string Text,
		string ProviderName,
		InferenceProviderType ProviderType,
		string ModelId,
		InferenceResponse Response,
		long DurationMs);

	/// <summary>
	/// The first reply from the enabled inference providers, tried in priority order so an offline
	/// provider falls through to the next, or null when none replies.
	/// </summary>
	private async Task<ModelReply?> AskAsync(string prompt, string systemPrompt, CancellationToken cancellationToken)
	{
		var providers = await _db.InferenceProviders
			.AsNoTracking()
			.Include(p => p.Models)
			.Where(p => p.IsEnabled)
			.ToListAsync(cancellationToken);

		foreach (var (provider, model) in InferenceTaskModels.Candidates(providers, TaskType))
		{
			InferenceResponse response;
			var startedAt = Stopwatch.GetTimestamp();
			try
			{
				response = await _inference.GenerateAsync(new InferenceRequest
				{
					TaskType = TaskType,
					Prompt = prompt,
					SystemPrompt = systemPrompt,
					ProviderId = provider.Id,
					Endpoint = provider.Endpoint,
					ProviderType = provider.ProviderType,
					Model = model.ModelId,
					Temperature = 0.2,
					MaxTokens = 300
				}, cancellationToken);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				_logger.LogWarning(ex, "Inference provider {ProviderName} failed to reply to an agent", provider.Name);
				continue;
			}

			if (!response.Success || string.IsNullOrWhiteSpace(response.Response))
			{
				_logger.LogWarning(
					"Inference provider {ProviderName} could not reply to an agent: {Error}",
					provider.Name,
					response.Error ?? "No response");
				continue;
			}

			return new ModelReply(
				response.Response,
				provider.Name,
				provider.ProviderType,
				response.ModelUsed ?? model.ModelId,
				response,
				response.DurationMs ?? (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
		}

		return null;
	}

	/// <summary>
	/// The message the agent receives: the answer, plus a note that the user is away so it stops
	/// asking.
	/// </summary>
	public static string FormatForAgent(AgentQuestionAnswer answer)
	{
		return $"{answer.Text}\n\n(Answered by VibeSwarm's inference model {answer.ModelId} while the user is away. "
			+ "Make any further decisions yourself and finish the task without asking again.)";
	}

	internal static string BuildPrompt(AgentQuestion question)
	{
		var builder = new StringBuilder();
		if (!string.IsNullOrWhiteSpace(question.ProjectName))
		{
			builder.Append("Project: ").AppendLine(question.ProjectName.Trim());
			builder.AppendLine();
		}

		builder.AppendLine("Goal the agent is working on:");
		var goal = question.GoalPrompt.Trim();
		builder.AppendLine(goal.Length > MaxGoalLength ? goal[..MaxGoalLength] + " …" : goal);
		builder.AppendLine();

		var recent = question.RecentOutput
			.Where(line => !string.IsNullOrWhiteSpace(line))
			.TakeLast(MaxRecentOutputLines)
			.ToList();
		if (recent.Count > 0)
		{
			builder.AppendLine("The agent's latest output:");
			foreach (var line in recent)
			{
				builder.AppendLine(line.TrimEnd());
			}
			builder.AppendLine();
		}

		builder.AppendLine("The agent asked:");
		builder.AppendLine(question.Question.Trim());

		if (question.Choices is { Count: > 0 })
		{
			builder.AppendLine();
			builder.AppendLine("Options it offered:");
			foreach (var choice in question.Choices)
			{
				builder.Append("- ").AppendLine(choice);
			}
		}

		return builder.ToString().Trim();
	}

	internal static string BuildFollowUpPrompt(Job job, string? finalMessage, string? notes)
	{
		var builder = new StringBuilder();
		if (!string.IsNullOrWhiteSpace(job.Project?.Name))
		{
			builder.Append("Project: ").AppendLine(job.Project.Name.Trim());
			builder.AppendLine();
		}

		builder.AppendLine("Goal the agent is working on:");
		var goal = job.GoalPrompt.Trim();
		builder.AppendLine(goal.Length > MaxGoalLength ? goal[..MaxGoalLength] + " …" : goal);
		builder.AppendLine();

		builder.Append("How the run ended: ");
		var error = job.ErrorMessage?.Trim();
		builder.AppendLine(job.Status switch
		{
			JobStatus.Failed when !string.IsNullOrEmpty(error)
				=> "it failed with this error: " + (error.Length > MaxErrorLength ? error[..MaxErrorLength] + " …" : error),
			JobStatus.Failed => "it failed.",
			JobStatus.Stalled => "it timed out before finishing.",
			_ => "it completed."
		});

		if (!string.IsNullOrWhiteSpace(finalMessage))
		{
			builder.AppendLine();
			builder.AppendLine("The agent's final message:");
			builder.AppendLine(TailOfFinalMessage(finalMessage.Trim())[0]);
		}

		var trimmedNotes = notes?.Trim();
		if (!string.IsNullOrEmpty(trimmedNotes))
		{
			builder.AppendLine();
			builder.AppendLine("The owner's notes for the message:");
			builder.AppendLine(trimmedNotes.Length > MaxNotesLength ? trimmedNotes[..MaxNotesLength] + " …" : trimmedNotes);
		}

		return builder.ToString().Trim();
	}

	/// <summary>
	/// The model's reply without reasoning blocks or wrapping quotes, or null when it is empty or
	/// hands the question back to the user.
	/// </summary>
	internal static string? CleanAnswer(string reply)
	{
		var text = ThinkBlockRegex().Replace(reply, string.Empty).Trim().Trim('"').Trim();
		if (text.Length == 0
			|| text.Contains(NeedsUserReply, StringComparison.OrdinalIgnoreCase)
			|| text.Contains(NoFollowUpReply, StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}

		return text;
	}

	/// <summary>
	/// What the agent said last in a run: its last text message, or the summary Copilot's
	/// autopilot hands to its <c>task_complete</c> tool when that came later, which is where
	/// Copilot puts its closing words. Raw output is used only when it is plain text, since a
	/// JSON event stream would only mislead the question check.
	/// </summary>
	public static string? FindFinalMessage(IReadOnlyList<ExecutionMessage> messages, string? output)
	{
		for (var index = messages.Count - 1; index >= 0; index--)
		{
			var message = messages[index];
			if (string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)
				&& !string.IsNullOrWhiteSpace(message.Content))
			{
				return message.Content;
			}

			if (!string.Equals(message.ToolName, TaskCompleteTool, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			if (string.Equals(message.Role, "tool_result", StringComparison.OrdinalIgnoreCase)
				&& !string.IsNullOrWhiteSpace(message.Content))
			{
				return message.Content;
			}

			if (string.Equals(message.Role, "tool_use", StringComparison.OrdinalIgnoreCase)
				&& ReadSummary(message.ToolInput) is { } summary)
			{
				return summary;
			}
		}

		return messages.Count == 0 && output?.TrimStart().StartsWith('{') == false ? output : null;
	}

	private const string TaskCompleteTool = "task_complete";

	private static string? ReadSummary(string? toolInput)
	{
		if (string.IsNullOrWhiteSpace(toolInput))
		{
			return null;
		}

		try
		{
			using var document = JsonDocument.Parse(toolInput);
			return document.RootElement.ValueKind == JsonValueKind.Object
				&& document.RootElement.TryGetProperty("summary", out var summary)
				&& summary.ValueKind == JsonValueKind.String
				&& !string.IsNullOrWhiteSpace(summary.GetString())
					? summary.GetString()
					: null;
		}
		catch (JsonException)
		{
			return null;
		}
	}

	/// <summary>
	/// The question an agent ended its final message with, or null when the message doesn't end
	/// by asking the user something. A CLI agent can't wait for a reply, so it finishes its run
	/// with the question instead ("Should I also add tests?").
	/// </summary>
	public static string? FindClosingQuestion(string? finalMessage)
	{
		if (string.IsNullOrWhiteSpace(finalMessage))
		{
			return null;
		}

		var lastParagraph = finalMessage
			.ReplaceLineEndings("\n")
			.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.LastOrDefault();
		var text = lastParagraph?.Trim('*', '_', '`', ' ').Trim();
		if (string.IsNullOrEmpty(text) || text.Length > MaxClosingQuestionLength)
		{
			return null;
		}

		return text.EndsWith('?') || ClosingOfferRegex().IsMatch(text) ? text : null;
	}

	/// <summary>
	/// The end of the agent's final message, which the answerer reads as its latest output.
	/// </summary>
	public static IReadOnlyList<string> TailOfFinalMessage(string finalMessage)
		=> [finalMessage.Length > MaxFinalMessageLength ? "…" + finalMessage[^MaxFinalMessageLength..] : finalMessage];

	[GeneratedRegex(@"<think>.*?(</think>|$)", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
	private static partial Regex ThinkBlockRegex();

	[GeneratedRegex(@"\b(would you like|do you want|should I|shall I|want me to|let me know (if|whether|which))\b", RegexOptions.IgnoreCase)]
	private static partial Regex ClosingOfferRegex();
}
