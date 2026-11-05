using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.VersionControl;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class LeadServiceTests : IDisposable
{
	private const string Reply = """
		The parser has no tests and two jobs failed on it.

		## IDEA: Add tests for the quote parser
		Why: src/Parser.cs has no tests.
		Size: S
		Goal: Cover the parser.
		Acceptance criteria:
		- Tests cover empty input

		## IDEA: **Retry failed price fetches**
		Why: Fetches fail silently.
		Size: m
		Goal: Retry with backoff.
		""";

	private readonly SqliteConnection _connection;
	private readonly DbContextOptions<VibeSwarmDbContext> _dbOptions;
	private readonly string _root = Path.Combine(Path.GetTempPath(), "vibeswarm-tests", Guid.NewGuid().ToString("N"));

	public LeadServiceTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();
		_dbOptions = new DbContextOptionsBuilder<VibeSwarmDbContext>().UseSqlite(_connection).Options;
		using var db = new VibeSwarmDbContext(_dbOptions);
		db.Database.EnsureCreated();
	}

	public void Dispose()
	{
		_connection.Dispose();
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}
	}

	[Fact]
	public void Parse_SplitsTheReportFromTheIdeas()
	{
		var reply = LeadPrompts.Parse(Reply);

		Assert.Equal("The parser has no tests and two jobs failed on it.", reply.Report);
		Assert.Equal(2, reply.Ideas.Count);
		Assert.Equal("Add tests for the quote parser", reply.Ideas[0].Title);
		Assert.Equal("S", reply.Ideas[0].Size);
		Assert.Contains("Acceptance criteria:", reply.Ideas[0].Spec);
		Assert.DoesNotContain("Retry", reply.Ideas[0].Spec);
		Assert.Equal("Retry failed price fetches", reply.Ideas[1].Title);
		Assert.Equal("M", reply.Ideas[1].Size);
	}

	[Fact]
	public void Parse_KeepsAtMostTheIdeaLimit_AndAReplyWithoutIdeasIsAllReport()
	{
		var many = string.Join("\n", Enumerable.Range(1, 12).Select(i => $"## IDEA: Idea {i}\nGoal: do {i}"));

		Assert.Equal(LeadReviewLimits.MaxIdeasPerReview, LeadPrompts.Parse(many).Ideas.Count);
		var none = LeadPrompts.Parse("Nothing worth doing right now.");
		Assert.Empty(none.Ideas);
		Assert.Equal("Nothing worth doing right now.", none.Report);
	}

	[Fact]
	public async Task Suggestions_StayOutOfTheIdeasList_UntilApproved_ThenJoinTheEndOfTheQueue()
	{
		var project = await SeedProjectAsync();
		await using (var db = CreateDb())
		{
			db.Ideas.Add(new Idea { ProjectId = project.Id, Description = "Typed by the owner", SortOrder = 4 });
			await db.SaveChangesAsync();
		}

		var service = CreateService(new ScriptedConsultService(_ => throw new InvalidOperationException()));
		var added = await service.AddSuggestionsAsync(project.Id, Guid.NewGuid(), LeadPrompts.Parse(Reply).Ideas, CancellationToken.None);
		Assert.Equal(2, added);

		await using (var db = CreateDb())
		{
			Assert.Equal(["Typed by the owner"], await db.Ideas.Select(i => i.Description).ToListAsync());
		}

		var status = await service.GetStatusAsync(project.Id);
		Assert.True(status.IsConfigured);
		Assert.Equal(["Add tests for the quote parser", "Retry failed price fetches"], status.Suggestions.Select(i => i.Description));

		var approved = await service.ApproveSuggestionAsync(status.Suggestions[0].Id);
		Assert.NotNull(approved);
		Assert.Equal(IdeaExpansionStatus.Approved, approved.ExpansionStatus);
		Assert.Equal(5, approved.SortOrder);

		await service.DismissSuggestionAsync(status.Suggestions[1].Id);

		await using (var db = CreateDb())
		{
			Assert.Equal(["Typed by the owner", "Add tests for the quote parser"], await db.Ideas.OrderBy(i => i.SortOrder).Select(i => i.Description).ToListAsync());
		}

		Assert.Empty((await service.GetStatusAsync(project.Id)).Suggestions);
	}

	[Fact]
	public async Task Suggestions_SkipIdeasTheProjectAlreadyHas_IncludingDismissedOnes()
	{
		var project = await SeedProjectAsync();
		await using (var db = CreateDb())
		{
			db.Ideas.Add(new Idea { ProjectId = project.Id, Description = "Retry failed price fetches.", NeedsApproval = true, DismissedAt = DateTime.UtcNow });
			await db.SaveChangesAsync();
		}

		var service = CreateService(new ScriptedConsultService(_ => throw new InvalidOperationException()));
		var added = await service.AddSuggestionsAsync(project.Id, Guid.NewGuid(), LeadPrompts.Parse(Reply).Ideas, CancellationToken.None);

		Assert.Equal(1, added);
	}

	[Fact]
	public async Task StartReview_NeedsALead()
	{
		var project = await SeedProjectAsync(withLead: false);
		var service = CreateService(new ScriptedConsultService(_ => throw new InvalidOperationException()));

		await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartReviewAsync(project.Id));
	}

	[Fact]
	public async Task Review_ReadsACopyOfTheBranch_AndSavesTheReportAndSuggestions()
	{
		var repo = await CreateRepoAsync();
		var project = await SeedProjectAsync(workingPath: repo, direction: "Make the parser solid.");
		var consult = new ScriptedConsultService(request =>
		{
			// The Lead reads a plain copy of the committed code, not the checkout itself.
			Assert.NotEqual(repo, request.WorkingDirectory);
			Assert.True(File.Exists(Path.Combine(request.WorkingDirectory!, "src", "Parser.cs")));
			Assert.False(Directory.Exists(Path.Combine(request.WorkingDirectory!, ".git")));
			Assert.Contains("Make the parser solid.", request.Prompt);
			return new ConsultResult { Success = true, Text = Reply, ProviderName = "Claude", CostUsd = 0.4m };
		});
		var service = CreateService(consult);

		var review = new LeadReview { ProjectId = project.Id };
		await using (var db = CreateDb())
		{
			db.LeadReviews.Add(review);
			await db.SaveChangesAsync();
		}

		await service.RunReviewAsync(review.Id, CancellationToken.None);

		Assert.Single(consult.Requests);
		await using var verify = CreateDb();
		var saved = await verify.LeadReviews.SingleAsync(r => r.Id == review.Id);
		Assert.Equal(LeadReviewStatus.Completed, saved.Status);
		Assert.Equal(2, saved.IdeasCreated);
		Assert.Equal(0.4m, saved.CostUsd);
		Assert.StartsWith("The parser has no tests", saved.Content);
		Assert.Equal(2, await verify.Ideas.IgnoreQueryFilters().CountAsync(i => i.LeadReviewId == review.Id && i.NeedsApproval));
		Assert.False(Directory.Exists(Path.Combine(_root, "lead", review.Id.ToString("N"))));
	}

	[Fact]
	public async Task Review_IsMarkedFailed_WhenTheLeadCannotRun()
	{
		var repo = await CreateRepoAsync();
		var project = await SeedProjectAsync(workingPath: repo);
		var service = CreateService(new ScriptedConsultService(_ => ConsultResult.Skip("Claude is near its usage limit")));
		var review = new LeadReview { ProjectId = project.Id };
		await using (var db = CreateDb())
		{
			db.LeadReviews.Add(review);
			await db.SaveChangesAsync();
		}

		await service.RunReviewAsync(review.Id, CancellationToken.None);

		await using var verify = CreateDb();
		var saved = await verify.LeadReviews.SingleAsync(r => r.Id == review.Id);
		Assert.Equal(LeadReviewStatus.Failed, saved.Status);
		Assert.Equal("Claude is near its usage limit", saved.Error);
		Assert.NotNull(saved.CompletedAt);
	}

	private LeadService CreateService(IReadOnlyConsultService consult)
	{
		var services = new ServiceCollection();
		services.AddDbContext<VibeSwarmDbContext>(options => options.UseSqlite(_connection));
		var provider = services.BuildServiceProvider();
		var runner = new LeadReviewRunner(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<LeadReviewRunner>.Instance);
		return new LeadService(CreateDb(), consult, new GitCommandExecutor(), new SystemCommandRunner(), runner, provider, NullLogger<LeadService>.Instance)
		{
			SnapshotRoot = Path.Combine(_root, "lead")
		};
	}

	private VibeSwarmDbContext CreateDb() => new(_dbOptions);

	private async Task<Project> SeedProjectAsync(bool withLead = true, string workingPath = "/tmp/none", string? direction = null)
	{
		await using var db = CreateDb();
		var provider = new Provider { Id = Guid.NewGuid(), Name = "Claude", Type = ProviderType.Claude, IsEnabled = true };
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Trader",
			WorkingPath = workingPath,
			LeadProviderId = withLead ? provider.Id : null,
			LeadDirection = direction
		};
		db.Providers.Add(provider);
		db.Projects.Add(project);
		await db.SaveChangesAsync();
		return project;
	}

	private async Task<string> CreateRepoAsync()
	{
		var repo = Path.Combine(_root, "repo");
		Directory.CreateDirectory(Path.Combine(repo, "src"));
		await File.WriteAllTextAsync(Path.Combine(repo, "src", "Parser.cs"), "class Parser {}\n");
		Git(repo, "init -q -b main");
		Git(repo, "add -A");
		Git(repo, "-c user.name=t -c user.email=t@t commit -q -m init");
		return repo;
	}

	private static void Git(string directory, string arguments)
	{
		var process = Process.Start(new ProcessStartInfo("git", arguments) { WorkingDirectory = directory, RedirectStandardError = true, RedirectStandardOutput = true })!;
		process.WaitForExit();
		Assert.Equal(0, process.ExitCode);
	}
}
