using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Inference;
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

		var providers = await _db.InferenceProviders
			.AsNoTracking()
			.Include(p => p.Models)
			.Where(p => p.IsEnabled)
			.ToListAsync(cancellationToken);

		var prompt = BuildPrompt(question);
		foreach (var (provider, model) in InferenceTaskModels.Candidates(providers, TaskType))
		{
			InferenceResponse response;
			try
			{
				response = await _inference.GenerateAsync(new InferenceRequest
				{
					TaskType = TaskType,
					Prompt = prompt,
					SystemPrompt = question.RunFinished ? SystemPrompt + RunFinishedSystemPromptAddition : SystemPrompt,
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
				_logger.LogWarning(ex, "Inference provider {ProviderName} failed to answer an agent question", provider.Name);
				continue;
			}

			if (!response.Success || string.IsNullOrWhiteSpace(response.Response))
			{
				_logger.LogWarning(
					"Inference provider {ProviderName} could not answer an agent question: {Error}",
					provider.Name,
					response.Error ?? "No response");
				continue;
			}

			var answer = CleanAnswer(response.Response);
			if (answer == null)
			{
				// The model judged that only the user can answer, or that nothing is left to do;
				// another model won't know better.
				return null;
			}

			return new AgentQuestionAnswer(answer, provider.Name, response.ModelUsed ?? model.ModelId);
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
