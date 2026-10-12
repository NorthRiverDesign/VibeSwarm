using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Inference;
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

	[Fact]
	public void FormatForAgent_TellsTheAgentTheUserIsAway()
	{
		var text = AgentQuestionAnswerer.FormatForAgent(new AgentQuestionAnswer("Use option 1.", "Desktop", "qwen3"));

		Assert.StartsWith("Use option 1.", text);
		Assert.Contains("qwen3", text);
		Assert.Contains("without asking again", text);
	}

	private static AgentQuestion CreateQuestion(
		string question = "Would you like me to also update the docs?",
		InteractionDetector.InteractionType type = InteractionDetector.InteractionType.Confirmation)
		=> new("Add a settings page", "VibeSwarm", question, type, null, ["Built the page."]);

	private static InferenceResponse Reply(string text) => new() { Success = true, Response = text };

	private AgentQuestionAnswerer CreateAnswerer(IInferenceService inference)
		=> new(CreateDbContext(), inference, NullLogger<AgentQuestionAnswerer>.Instance);

	private VibeSwarmDbContext CreateDbContext() => new(_dbOptions);

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
