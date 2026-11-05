using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class JobReviewTests : IDisposable
{
	private const string Diff = " src/app.cs | 2 +-\n--- a/src/app.cs\n+++ b/src/app.cs\n-old\n+new\n";

	private readonly SqliteConnection _connection;
	private readonly DbContextOptions<VibeSwarmDbContext> _dbOptions;

	public JobReviewTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();
		_dbOptions = new DbContextOptionsBuilder<VibeSwarmDbContext>().UseSqlite(_connection).Options;
		using var db = new VibeSwarmDbContext(_dbOptions);
		db.Database.EnsureCreated();
	}

	public void Dispose() => _connection.Dispose();

	[Fact]
	public async Task Findings_GoBackToTheAgent_AndCountAsARound()
	{
		var job = await SeedJobAsync();
		var consult = new ScriptedConsultService(_ => new ConsultResult { Success = true, Text = "Looked at app.cs.\nISSUES:\n1. src/app.cs drops the null check.", ProviderName = "Copilot", CostUsd = 0.05m });

		var feedback = await CreateService(consult).TryGetReviewFeedbackAsync(job, "/tmp/repo", "abc123", "session-1", canSkipFinalBuild: true, CancellationToken.None);

		Assert.NotNull(feedback);
		Assert.Contains("drops the null check", feedback);
		Assert.Contains("Copilot", feedback);
		var request = Assert.Single(consult.Requests);
		Assert.Contains("Add a greeting", request.Prompt);
		Assert.Contains("+new", request.Prompt);
		Assert.Equal(job.Id, request.JobId);
		Assert.Equal("/tmp/repo", request.WorkingDirectory);

		var saved = await LoadAsync(job.Id);
		Assert.Equal(1, saved.ReviewRounds);
		Assert.Equal(JobReviewStatus.None, saved.ReviewStatus);
		Assert.Contains("drops the null check", saved.ReviewNotes);
		Assert.Equal(0.05m, saved.ReviewCostUsd);
	}

	[Fact]
	public async Task Approval_DeliversTheWork_AndRecordsIt()
	{
		var job = await SeedJobAsync(rounds: 1);
		var consult = new ScriptedConsultService(_ => new ConsultResult { Success = true, Text = "All good.\nREVIEW_APPROVED", ProviderName = "Copilot" });

		var feedback = await CreateService(consult).TryGetReviewFeedbackAsync(job, "/tmp/repo", "abc123", "session-1", canSkipFinalBuild: true, CancellationToken.None);

		Assert.Null(feedback);
		var saved = await LoadAsync(job.Id);
		Assert.Equal(JobReviewStatus.Approved, saved.ReviewStatus);
		Assert.Equal(1, saved.ReviewRounds);
		Assert.Equal("Copilot", saved.ReviewProviderName);
	}

	[Fact]
	public async Task AReviewerThatCannotRun_NeverHoldsTheWorkBack()
	{
		var job = await SeedJobAsync();
		var consult = new ScriptedConsultService(_ => ConsultResult.Skip("Copilot is near its usage limit", "Copilot"));

		var feedback = await CreateService(consult).TryGetReviewFeedbackAsync(job, "/tmp/repo", "abc123", "session-1", canSkipFinalBuild: true, CancellationToken.None);

		Assert.Null(feedback);
		var saved = await LoadAsync(job.Id);
		Assert.Equal(JobReviewStatus.Skipped, saved.ReviewStatus);
		Assert.Equal("Copilot is near its usage limit", saved.ReviewNotes);
	}

	[Fact]
	public async Task AReviewerThatThrows_NeverHoldsTheWorkBack()
	{
		var job = await SeedJobAsync();
		var consult = new ScriptedConsultService(_ => throw new InvalidOperationException("boom"));

		var feedback = await CreateService(consult).TryGetReviewFeedbackAsync(job, "/tmp/repo", "abc123", "session-1", canSkipFinalBuild: true, CancellationToken.None);

		Assert.Null(feedback);
		Assert.Equal(JobReviewStatus.Skipped, (await LoadAsync(job.Id)).ReviewStatus);
	}

	[Fact]
	public async Task OnceTheRoundsRunOut_TheWorkIsDeliveredWithoutAnotherReview()
	{
		var job = await SeedJobAsync(rounds: JobReview.MaxRounds, notes: "1. Still broken");
		var consult = new ScriptedConsultService(_ => throw new InvalidOperationException("No review expected"));

		var feedback = await CreateService(consult).TryGetReviewFeedbackAsync(job, "/tmp/repo", "abc123", "session-1", canSkipFinalBuild: true, CancellationToken.None);

		Assert.Null(feedback);
		Assert.Empty(consult.Requests);
		var saved = await LoadAsync(job.Id);
		Assert.Equal(JobReviewStatus.Unresolved, saved.ReviewStatus);
		Assert.Equal("1. Still broken", saved.ReviewNotes);
	}

	[Fact]
	public async Task NothingIsReviewed_WhenReviewIsOff()
	{
		var job = await SeedJobAsync(reviewEnabled: false);
		var consult = new ScriptedConsultService(_ => throw new InvalidOperationException("No review expected"));

		var feedback = await CreateService(consult).TryGetReviewFeedbackAsync(job, "/tmp/repo", "abc123", "session-1", canSkipFinalBuild: true, CancellationToken.None);

		Assert.Null(feedback);
		Assert.Empty(consult.Requests);
		Assert.Equal(JobReviewStatus.None, (await LoadAsync(job.Id)).ReviewStatus);
	}

	[Fact]
	public async Task AJobWithoutASession_IsSkipped_BecauseNobodyCouldFixTheFindings()
	{
		var job = await SeedJobAsync();
		var consult = new ScriptedConsultService(_ => throw new InvalidOperationException("No review expected"));

		var feedback = await CreateService(consult).TryGetReviewFeedbackAsync(job, "/tmp/repo", "abc123", sessionId: null, canSkipFinalBuild: true, CancellationToken.None);

		Assert.Null(feedback);
		Assert.Equal(JobReviewStatus.Skipped, (await LoadAsync(job.Id)).ReviewStatus);
	}

	[Fact]
	public async Task AnEmptyDiff_IsSkipped()
	{
		var job = await SeedJobAsync();
		var consult = new ScriptedConsultService(_ => throw new InvalidOperationException("No review expected"));

		var feedback = await CreateService(consult, diff: "").TryGetReviewFeedbackAsync(job, "/tmp/repo", "abc123", "session-1", canSkipFinalBuild: true, CancellationToken.None);

		Assert.Null(feedback);
		Assert.Equal(JobReviewStatus.Skipped, (await LoadAsync(job.Id)).ReviewStatus);
	}

	[Theory]
	[InlineData("Everything checks out.\nREVIEW_APPROVED", true)]
	[InlineData("**ISSUES:**\n1. Missing test", false)]
	[InlineData("ISSUES:\n1. Missing test\n\nOn second look this is fine.\nREVIEW_APPROVED", true)]
	[InlineData("REVIEW_APPROVED would be wrong here.\nISSUES:\n1. Broken parser", false)]
	[InlineData("<think>REVIEW_APPROVED?</think>\nISSUES:\n1. Broken parser", false)]
	[InlineData("ISSUES:\n1. Missing null check\n\nOnce fixed this would be REVIEW_APPROVED.", false)]
	[InlineData("All good.\n**REVIEW_APPROVED**", true)]
	public void ParseVerdict_ReadsTheLastMarker(string reply, bool approved)
	{
		var verdict = ReviewPrompts.ParseVerdict(reply);

		Assert.NotNull(verdict);
		Assert.Equal(approved, verdict.Approved);
		if (!approved)
		{
			Assert.StartsWith("1.", verdict.Issues);
		}
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("I looked at it and it seems fine overall.")]
	public void ParseVerdict_IsNull_WhenTheReviewerDecidedNothing(string? reply)
	{
		Assert.Null(ReviewPrompts.ParseVerdict(reply));
	}

	[Fact]
	public void ReviewPrompt_CutsALongDiff_AndPointsAtTheFiles()
	{
		var prompt = ReviewPrompts.BuildReviewPrompt("Goal", "Plan", new string('x', ReviewPrompts.MaxDiffLength + 100), canOpenFiles: true);

		Assert.Contains("cut off", prompt);
		Assert.Contains("Open the files", prompt);
		Assert.True(prompt.Length < ReviewPrompts.MaxDiffLength + 2_000);
	}

	[Fact]
	public void Reset_ClearsAnEarlierRunsReview()
	{
		var job = new Job { ReviewStatus = JobReviewStatus.Unresolved, ReviewRounds = 2, ReviewNotes = "x", ReviewCostUsd = 1m, BuildCheckedTreeHash = "abc" };

		JobReview.Reset(job);

		Assert.Equal(JobReviewStatus.None, job.ReviewStatus);
		Assert.Equal(0, job.ReviewRounds);
		Assert.Null(job.ReviewNotes);
		Assert.Null(job.ReviewCostUsd);
		Assert.Null(job.BuildCheckedTreeHash);
	}

	private JobProcessingService CreateService(IReadOnlyConsultService consult, string diff = Diff)
	{
		var services = new ServiceCollection();
		services.AddDbContext<VibeSwarmDbContext>(options => options.UseSqlite(_connection));
		services.AddSingleton(consult);
		var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
		return new JobProcessingService(scopeFactory, NullLogger<JobProcessingService>.Instance, new DiffVersionControlService(diff), projectEnvironmentCredentialService: new NoCredentials());
	}

	private async Task<Job> SeedJobAsync(bool reviewEnabled = true, int rounds = 0, string? notes = null)
	{
		await using var db = new VibeSwarmDbContext(_dbOptions);
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "App",
			WorkingPath = "/tmp/repo",
			ReviewEnabled = reviewEnabled,
			ReviewProviderId = Guid.NewGuid()
		};
		var job = new Job
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			Title = "Greeting",
			GoalPrompt = "Add a greeting",
			Status = JobStatus.Processing,
			ReviewRounds = rounds,
			ReviewNotes = notes
		};
		var provider = new Provider { Id = Guid.NewGuid(), Name = "Claude", Type = ProviderType.Claude, IsEnabled = true };
		job.ProviderId = provider.Id;
		db.Providers.Add(provider);
		db.Projects.Add(project);
		db.Jobs.Add(job);
		await db.SaveChangesAsync();
		job.Project = project;
		return job;
	}

	private async Task<Job> LoadAsync(Guid jobId)
	{
		await using var db = new VibeSwarmDbContext(_dbOptions);
		return await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
	}

	private sealed class NoCredentials : IProjectEnvironmentCredentialService
	{
		public void PrepareForStorage(Project project, IReadOnlyCollection<ProjectEnvironment>? existingEnvironments = null) { }
		public void PopulateForEditing(Project? project) { }
		public void PopulateForExecution(Project? project) { }
		public Dictionary<string, string>? BuildJobEnvironmentVariables(Project? project) => null;
	}

	private sealed class DiffVersionControlService(string diff) : FakeVersionControlServiceBase
	{
		public override Task<string?> GetWorkingDirectoryDiffAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default)
			=> Task.FromResult<string?>(diff);
	}
}
