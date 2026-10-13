using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Inference;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.Utilities;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class AgentQuestionAnswererTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly DbContextOptions<VibeSwarmDbContext> _dbOptions;

	public AgentQuestionAnswererTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();
		_dbOptions = new DbContextOptionsBuilder<VibeSwarmDbContext>()
			.UseSqlite(_connection)
			.Options;

		using var dbContext = CreateDbContext();
		dbContext.Database.EnsureCreated();
	}

	public void Dispose() => _connection.Dispose();

	[Fact]
	public async Task TryAnswerAsync_UsesTheFirstProviderByPriority_AndItsAgentAnswerModel()
	{
		await SeedProviderAsync("Second", priority: 1, ("big-model", "default"));
		await SeedProviderAsync("First", priority: 0, ("general", "default"), ("answerer", AgentQuestionAnswerer.TaskType));
		var inference = new ScriptedInferenceService(_ => Reply("Yes, go ahead with option 2."));

		var answer = await CreateAnswerer(inference).TryAnswerAsync(CreateQuestion());

		Assert.NotNull(answer);
		Assert.Equal("Yes, go ahead with option 2.", answer.Text);
		Assert.Equal("First", answer.ProviderName);
		Assert.Equal("answerer", answer.ModelId);
		var request = Assert.Single(inference.Requests);
		Assert.Equal("answerer", request.Model);
		Assert.Equal(AgentQuestionAnswerer.TaskType, request.TaskType);
	}

	[Fact]
	public async Task TryAnswerAsync_FallsThroughToTheNextProvider_WhenOneIsOffline()
	{
		await SeedProviderAsync("Desktop", priority: 0, ("local", "default"));
		await SeedProviderAsync("Cloud", priority: 1, ("hosted", "default"));
		var inference = new ScriptedInferenceService(request => request.Model == "local"
			? new InferenceResponse { Success = false, Error = "Connection refused" }
			: Reply("Proceed."));

		var answer = await CreateAnswerer(inference).TryAnswerAsync(CreateQuestion());

		Assert.Equal("Cloud", answer?.ProviderName);
		Assert.Equal(2, inference.Requests.Count);
	}

	[Fact]
	public async Task TryAnswerAsync_LeavesTheQuestionForTheUser_WhenTheModelSaysOnlyTheyCanAnswer()
	{
		await SeedProviderAsync("First", priority: 0, ("a", "default"));
		await SeedProviderAsync("Second", priority: 1, ("b", "default"));
		var inference = new ScriptedInferenceService(_ => Reply(AgentQuestionAnswerer.NeedsUserReply));

		var answer = await CreateAnswerer(inference).TryAnswerAsync(CreateQuestion());

		Assert.Null(answer);
		Assert.Single(inference.Requests);
	}

	[Fact]
	public async Task TryAnswerAsync_NeverAnswersCredentialPrompts()
	{
		await SeedProviderAsync("First", priority: 0, ("a", "default"));
		var inference = new ScriptedInferenceService(_ => Reply("hunter2"));

		var answer = await CreateAnswerer(inference).TryAnswerAsync(
			CreateQuestion("Password:", InteractionDetector.InteractionType.Authentication));

		Assert.Null(answer);
		Assert.Empty(inference.Requests);
	}

	[Fact]
	public async Task TryAnswerAsync_DoesNothing_WhenTheSettingIsOff()
	{
		await SeedProviderAsync("First", priority: 0, ("a", "default"));
		await using (var dbContext = CreateDbContext())
		{
			dbContext.AppSettings.Add(new AppSettings { Id = Guid.NewGuid(), InferenceAnswersAgentQuestions = false });
			await dbContext.SaveChangesAsync();
		}
		var inference = new ScriptedInferenceService(_ => Reply("Proceed."));

		var answer = await CreateAnswerer(inference).TryAnswerAsync(CreateQuestion());

		Assert.Null(answer);
		Assert.Empty(inference.Requests);
	}

	[Fact]
	public async Task TryAnswerAsync_SkipsDisabledProvidersAndProvidersWithoutADefaultModel()
	{
		await SeedProviderAsync("Disabled", 0, false, ("a", "default"));
		await SeedProviderAsync("No default", 1, ("b", "suggest"));
		var inference = new ScriptedInferenceService(_ => Reply("Proceed."));

		var answer = await CreateAnswerer(inference).TryAnswerAsync(CreateQuestion());

		Assert.Null(answer);
		Assert.Empty(inference.Requests);
	}

	[Theory]
	[InlineData("<think>The goal says tests.</think>\nAdd the tests too.", "Add the tests too.")]
	[InlineData("\"Use PostgreSQL.\"", "Use PostgreSQL.")]
	[InlineData("<think>still thinking", null)]
	[InlineData("needs_user", null)]
	public void CleanAnswer_StripsReasoningAndQuotes(string reply, string? expected)
	{
		Assert.Equal(expected, AgentQuestionAnswerer.CleanAnswer(reply));
	}

	[Fact]
	public void BuildPrompt_GivesTheModelTheGoalTheQuestionAndTheOptions()
	{
		var prompt = AgentQuestionAnswerer.BuildPrompt(new AgentQuestion(
			"Add dark mode",
			"TradeNugget",
			"Which approach should I use?",
			InteractionDetector.InteractionType.Choice,
			["CSS variables", "A second stylesheet"],
			["Looked at the theme files.", "", "Which approach should I use?"]));

		Assert.Contains("Project: TradeNugget", prompt);
		Assert.Contains("Add dark mode", prompt);
		Assert.Contains("Looked at the theme files.", prompt);
		Assert.Contains("- CSS variables", prompt);
		Assert.EndsWith("- A second stylesheet", prompt);
	}

	[Theory]
	[InlineData("Added the page and its tests.\n\nShould I also update the README?", "Should I also update the README?")]
	[InlineData("Done.\n\n**Want me to wire it into the nav as well?**", "Want me to wire it into the nav as well?")]
	[InlineData("All green.\n\nLet me know if you'd like the dark theme too.", "Let me know if you'd like the dark theme too.")]
	[InlineData("Why did the build fail? A missing package.\n\nFixed it and the build passes.", null)]
	[InlineData("Implemented the change. Tests pass.", null)]
	[InlineData("", null)]
	public void FindClosingQuestion_FindsAQuestionOnlyAtTheEnd(string finalMessage, string? expected)
	{
		Assert.Equal(expected, AgentQuestionAnswerer.FindClosingQuestion(finalMessage));
	}

	[Fact]
	public void FindFinalMessage_ReadsCopilotsTaskCompleteSummary()
	{
		// The shape a real Copilot CLI 1.0.95 autopilot run reported: the closing words go to the
		// task_complete tool, after an opening assistant message.
		const string summary = "Created hello.txt.\n\nShould I also add a README.md that explains what hello.txt is for?";
		List<ExecutionMessage> messages =
		[
			new() { Role = "assistant", Content = "Creating hello.txt, then verifying it." },
			new() { Role = "tool_use", ToolName = "create", Content = "create" },
			new() { Role = "tool_result", ToolName = "create", Content = "Created file hello.txt" },
			new() { Role = "tool_use", ToolName = "task_complete", Content = "task_complete", ToolInput = "{\"summary\":\"Created hello.txt.\\n\\nShould I also add a README.md that explains what hello.txt is for?\"}" },
			new() { Role = "tool_result", ToolName = "task_complete", Content = summary }
		];

		var finalMessage = AgentQuestionAnswerer.FindFinalMessage(messages, "{\"type\":\"result\"}");

		Assert.Equal(summary, finalMessage);
		Assert.Equal("Should I also add a README.md that explains what hello.txt is for?", AgentQuestionAnswerer.FindClosingQuestion(finalMessage));
		Assert.Equal(summary, AgentQuestionAnswerer.FindFinalMessage(messages[..4], null));
	}

	[Fact]
	public void FindFinalMessage_PrefersALaterAssistantMessage_AndNeverReadsAJsonStream()
	{
		List<ExecutionMessage> messages =
		[
			new() { Role = "tool_result", ToolName = "task_complete", Content = "Earlier summary?" },
			new() { Role = "assistant", Content = "All done." }
		];

		Assert.Equal("All done.", AgentQuestionAnswerer.FindFinalMessage(messages, null));
		Assert.Null(AgentQuestionAnswerer.FindFinalMessage([], "{\"type\":\"assistant.message\",\"data\":{\"content\":\"Should I?\"}}"));
		Assert.Equal("Done. Should I push?", AgentQuestionAnswerer.FindFinalMessage([], "Done. Should I push?"));
	}

	[Fact]
	public void FindClosingQuestion_IgnoresALongClosingReport()
	{
		Assert.Null(AgentQuestionAnswerer.FindClosingQuestion(new string('x', 700) + "?"));
	}

	[Fact]
	public async Task TryAnswerAsync_TellsTheModelItMayEndTheJob_WhenTheRunHasFinished()
	{
		await SeedProviderAsync("First", priority: 0, ("a", "default"));
		var inference = new ScriptedInferenceService(_ => Reply(AgentQuestionAnswerer.NoFollowUpReply));

		var answer = await CreateAnswerer(inference).TryAnswerAsync(CreateQuestion() with { RunFinished = true });

		Assert.Null(answer);
		Assert.Contains(AgentQuestionAnswerer.NoFollowUpReply, Assert.Single(inference.Requests).SystemPrompt);
	}

	[Fact]
	public void FormatForAgent_TellsTheAgentTheUserIsAway()
	{
		var text = AgentQuestionAnswerer.FormatForAgent(new AgentQuestionAnswer("Use option 1.", "Desktop", "qwen3"));

		Assert.StartsWith("Use option 1.", text);
		Assert.Contains("qwen3", text);
		Assert.Contains("without asking again", text);
	}

	[Fact]
	public async Task DraftFollowUpAsync_WritesAReplyFromTheFinalMessageAndNotes_EvenWhenAutomaticAnswersAreOff()
	{
		await SeedProviderAsync("Desktop", priority: 0, ("qwen3", "default"));
		await using (var dbContext = CreateDbContext())
		{
			dbContext.AppSettings.Add(new AppSettings { Id = Guid.NewGuid(), InferenceAnswersAgentQuestions = false });
			await dbContext.SaveChangesAsync();
		}
		var jobId = await SeedJobAsync(JobStatus.Failed, "dotnet test failed", "Tests fail on the new page. Should I mock the clock?");
		var inference = new ScriptedInferenceService(_ => Reply("<think>Notes say mock it.</think>Yes, mock the clock and rerun the tests."));

		var draft = await CreateAnswerer(inference).DraftFollowUpAsync(jobId, "  mock it  ");

		Assert.Null(draft.Error);
		Assert.Equal("Yes, mock the clock and rerun the tests.", draft.Text);
		Assert.Equal("Desktop", draft.ProviderName);
		Assert.Equal("qwen3", draft.ModelId);
		var request = Assert.Single(inference.Requests);
		Assert.Equal(AgentQuestionAnswerer.FollowUpSystemPrompt, request.SystemPrompt);
		Assert.Contains("Add a settings page", request.Prompt);
		Assert.Contains("failed with this error: dotnet test failed", request.Prompt);
		Assert.Contains("Should I mock the clock?", request.Prompt);
		Assert.EndsWith("The owner's notes for the message:\nmock it", request.Prompt.ReplaceLineEndings("\n"));
	}

	[Fact]
	public async Task DraftFollowUpAsync_DoesNotAskInference_WhileTheJobIsRunning()
	{
		await SeedProviderAsync("Desktop", priority: 0, ("qwen3", "default"));
		var jobId = await SeedJobAsync(JobStatus.Processing, null, "Working on it.");
		var inference = new ScriptedInferenceService(_ => Reply("Proceed."));

		var draft = await CreateAnswerer(inference).DraftFollowUpAsync(jobId, null);

		Assert.Null(draft.Text);
		Assert.Contains("still running", draft.Error);
		Assert.Empty(inference.Requests);
	}

	[Fact]
	public async Task DraftFollowUpAsync_ExplainsWhyThereIsNoDraft()
	{
		var jobId = await SeedJobAsync(JobStatus.Completed, null, "Done. Should I deploy to production?");

		var noProvider = await CreateAnswerer(new ScriptedInferenceService(_ => Reply("Deploy."))).DraftFollowUpAsync(jobId, null);
		await SeedProviderAsync("Desktop", priority: 0, ("qwen3", "default"));
		var declined = await CreateAnswerer(new ScriptedInferenceService(_ => Reply(AgentQuestionAnswerer.NeedsUserReply))).DraftFollowUpAsync(jobId, null);

		Assert.Null(noProvider.Text);
		Assert.Contains("No inference provider replied", noProvider.Error);
		Assert.Null(declined.Text);
		Assert.Contains("only you can make", declined.Error);
	}

	private static AgentQuestion CreateQuestion(
		string question = "Would you like me to also update the docs?",
		InteractionDetector.InteractionType type = InteractionDetector.InteractionType.Confirmation)
		=> new("Add a settings page", "VibeSwarm", question, type, null, ["Built the page."]);

	private static InferenceResponse Reply(string text) => new() { Success = true, Response = text };

	private AgentQuestionAnswerer CreateAnswerer(IInferenceService inference)
		=> new(CreateDbContext(), inference, NullLogger<AgentQuestionAnswerer>.Instance);

	private VibeSwarmDbContext CreateDbContext() => new(_dbOptions);

	private async Task<Guid> SeedJobAsync(JobStatus status, string? errorMessage, string finalMessage)
	{
		await using var dbContext = CreateDbContext();
		var project = new Project { Id = Guid.NewGuid(), Name = "VibeSwarm", WorkingPath = "/tmp/vibeswarm" };
		var provider = new Provider { Id = Guid.NewGuid(), Name = "Claude", Type = ProviderType.Claude, IsEnabled = true };
		var job = new Job
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			ProviderId = provider.Id,
			GoalPrompt = "Add a settings page",
			Status = status,
			ErrorMessage = errorMessage
		};
		dbContext.Projects.Add(project);
		dbContext.Providers.Add(provider);
		dbContext.Jobs.Add(job);
		dbContext.JobMessages.Add(new JobMessage { Id = Guid.NewGuid(), JobId = job.Id, Role = MessageRole.Assistant, Content = "Started on the page.", CreatedAt = DateTime.UtcNow.AddMinutes(-5) });
		dbContext.JobMessages.Add(new JobMessage { Id = Guid.NewGuid(), JobId = job.Id, Role = MessageRole.Assistant, Content = finalMessage, CreatedAt = DateTime.UtcNow });
		await dbContext.SaveChangesAsync();
		return job.Id;
	}

	private Task SeedProviderAsync(string name, int priority, params (string ModelId, string TaskType)[] models)
		=> SeedProviderAsync(name, priority, true, models);

	private async Task SeedProviderAsync(string name, int priority, bool isEnabled, params (string ModelId, string TaskType)[] models)
	{
		await using var dbContext = CreateDbContext();
		var provider = new InferenceProvider
		{
			Name = name,
			Endpoint = $"http://{name.Replace(' ', '-').ToLowerInvariant()}:11434",
			Priority = priority,
			IsEnabled = isEnabled
		};
		foreach (var (modelId, taskType) in models)
		{
			provider.Models.Add(new InferenceModel { ModelId = modelId, TaskType = taskType, IsDefault = true });
		}

		dbContext.InferenceProviders.Add(provider);
		await dbContext.SaveChangesAsync();
	}

	private sealed class ScriptedInferenceService(Func<InferenceRequest, InferenceResponse> respond) : IInferenceService
	{
		public List<InferenceRequest> Requests { get; } = [];

		public Task<InferenceResponse> GenerateAsync(InferenceRequest request, CancellationToken ct = default)
		{
			Requests.Add(request);
			return Task.FromResult(respond(request));
		}

		public Task<InferenceHealthResult> ProbeAsync(InferenceProbeRequest request, CancellationToken ct = default) => throw new NotSupportedException();
		public Task<InferenceHealthResult> CheckHealthAsync(string? endpoint = null, InferenceProviderType? providerType = null, CancellationToken ct = default) => throw new NotSupportedException();
		public Task<List<DiscoveredModel>> GetAvailableModelsAsync(string? endpoint = null, InferenceProviderType? providerType = null, CancellationToken ct = default) => throw new NotSupportedException();
		public Task<InferenceResponse> GenerateForTaskAsync(string taskType, string prompt, string? systemPrompt = null, CancellationToken ct = default) => throw new NotSupportedException();
	}
}
