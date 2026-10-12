using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Inference;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.Utilities;

namespace VibeSwarm.Web.Services;

/// <summary>
/// A question a running agent stopped to ask, with what the answerer needs to reply to it.
/// </summary>
public sealed record AgentQuestion(
	string GoalPrompt,
	string? ProjectName,
	string Question,
	InteractionDetector.InteractionType Type,
	IReadOnlyList<string>? Choices,
	IReadOnlyList<string> RecentOutput);

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
	public const string TaskType = "agent-answer";

	/// <summary>
	/// Answers given in one job before further questions wait for the user, so an agent that keeps
	/// asking can't loop on inference.
	/// </summary>
	public const int MaxAnswersPerJob = 3;

	/// <summary>The reply that tells VibeSwarm to leave the question for the user.</summary>
	internal const string NeedsUserReply = "NEEDS_USER";

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
			.OrderBy(p => p.Priority)
			.ThenBy(p => p.Name)
			.ToListAsync(cancellationToken);

		var prompt = BuildPrompt(question);
		foreach (var provider in providers)
		{
			var model = ResolveModel(provider);
			if (model == null)
			{
				continue;
			}

			InferenceResponse response;
			try
			{
				response = await _inference.GenerateAsync(new InferenceRequest
				{
					TaskType = TaskType,
					Prompt = prompt,
					SystemPrompt = SystemPrompt,
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
				// The model judged that only the user can answer; another model won't know better.
				return null;
			}

			return new AgentQuestionAnswer(answer, provider.Name, response.ModelUsed ?? model.ModelId);
		}

		return null;
	}

	/// <summary>
	/// The model that answers for <paramref name="provider"/>: its <see cref="TaskType"/> model, or its
	/// default one.
	/// </summary>
	internal static InferenceModel? ResolveModel(InferenceProvider provider)
	{
		return provider.Models
			.Where(m => m.IsAvailable && m.IsDefault && string.Equals(m.TaskType, TaskType, StringComparison.OrdinalIgnoreCase))
			.OrderBy(m => m.ModelId)
			.FirstOrDefault()
			?? provider.Models
				.Where(m => m.IsAvailable && m.IsDefault && string.Equals(m.TaskType, "default", StringComparison.OrdinalIgnoreCase))
				.OrderBy(m => m.ModelId)
				.FirstOrDefault();
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
		if (text.Length == 0 || text.Contains(NeedsUserReply, StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}

		return text;
	}

	[GeneratedRegex(@"<think>.*?(</think>|$)", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
	private static partial Regex ThinkBlockRegex();
}
