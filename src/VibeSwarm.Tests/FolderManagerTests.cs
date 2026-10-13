using Bunit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Client.Components.Projects;
using VibeSwarm.Client.Pages;
using VibeSwarm.Client.Services;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Inference;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.VersionControl;
using VibeSwarm.Shared.VersionControl.Models;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class FolderManagerServiceTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly DbContextOptions<VibeSwarmDbContext> _dbOptions;
	private readonly string _workingPath;

	public FolderManagerServiceTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();
		_dbOptions = new DbContextOptionsBuilder<VibeSwarmDbContext>()
			.UseSqlite(_connection)
			.Options;
		_workingPath = Path.Combine(Path.GetTempPath(), $"vibeswarm-manager-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_workingPath);

		using var dbContext = CreateDbContext();
		dbContext.Database.EnsureCreated();
	}

	public void Dispose()
	{
		_connection.Dispose();
		Directory.Delete(_workingPath, recursive: true);
	}

	[Fact]
	public async Task RunAsync_ReadsEveryProjectInTheFolder_AndSavesTheReport()
	{
		await SeedProviderAsync();
		var folderId = await SeedFolderAsync("Shop");
		var webId = await SeedProjectAsync("Web", folderId, _workingPath);
		await SeedProjectAsync("Api", folderId, "/does/not/exist");
		await SeedProjectAsync("Elsewhere", null, "/does/not/exist");
		await File.WriteAllTextAsync(Path.Combine(_workingPath, "TODO.md"), "- Ship dark mode");
		await using (var dbContext = CreateDbContext())
		{
			var agent = new Provider { Id = Guid.NewGuid(), Name = "Claude", Type = ProviderType.Claude, IsEnabled = true };
			dbContext.Providers.Add(agent);
			dbContext.Ideas.AddRange(
				new Idea { ProjectId = webId, Description = "Add a checkout page", SortOrder = 1 },
				new Idea { ProjectId = webId, Description = "Already running idea", IsProcessing = true });
			dbContext.Jobs.AddRange(
				NewJob(webId, agent.Id, "Fix login", JobStatus.Completed, summary: "Login works again", commit: "abc1234"),
				NewJob(webId, agent.Id, "Speed up search", JobStatus.Failed, error: "dotnet test failed"),
				NewJob(webId, agent.Id, "Add cart", JobStatus.Processing));
			await dbContext.SaveChangesAsync();
		}
		var inference = new ScriptedInferenceService(_ => new InferenceResponse
		{
			Success = true,
			Response = "<think>Reading the facts.</think>\nOverview\nThe shop is on track.",
			PromptTokens = 900,
			CompletionTokens = 120,
			DurationMs = 4000,
			CostUsd = 0.002m
		});

		var result = await CreateService(inference).RunAsync(folderId, new FolderManagerRunRequest { Notes = "  Ready to release?  " });

		Assert.Null(result.Error);
		var report = Assert.IsType<FolderManagerReport>(result.Report);
		Assert.Equal("Overview\nThe shop is on track.", report.Content);
		Assert.Equal("Ready to release?", report.Notes);
		Assert.Equal("Api, Web", report.ProjectNames);
		Assert.Equal("Desktop", report.ProviderName);
		Assert.Equal("qwen3", report.ModelId);
		Assert.Equal(900, report.PromptTokens);
		Assert.Equal(0.002m, report.CostUsd);
		Assert.Null(report.PreviousReportId);

		var request = Assert.Single(inference.Requests);
		Assert.Equal(FolderManagerService.SystemPrompt, request.SystemPrompt);
		Assert.Equal(InferenceTaskModels.ProjectManager, request.TaskType);
		Assert.Contains("Folder: Shop", request.Prompt);
		Assert.Contains("## Project: Api", request.Prompt);
		Assert.Contains("## Project: Web", request.Prompt);
		Assert.DoesNotContain("Elsewhere", request.Prompt);
		Assert.Contains("- Add a checkout page", request.Prompt);
		Assert.DoesNotContain("Already running idea", request.Prompt);
		Assert.Contains("To-do file TODO.md in the repository:\n- Ship dark mode", request.Prompt.ReplaceLineEndings("\n"));
		Assert.Contains("[Processing] Add cart", request.Prompt);
		Assert.Contains("[Completed] Fix login | outcome: Login works again", request.Prompt);
		Assert.Contains("[Failed] Speed up search | error: dotnet test failed", request.Prompt);
		Assert.Contains("Branch: main...origin/main [ahead 1]", request.Prompt);
		Assert.Contains("Working tree: 2 uncommitted files.", request.Prompt);
		Assert.Contains("- 2026-10-12 abc1234 Fix the login form", request.Prompt);
		Assert.EndsWith("# What the owner wants this report to look at:\nReady to release?", request.Prompt.ReplaceLineEndings("\n"));

		await using var verify = CreateDbContext();
		Assert.Equal(report.Id, Assert.Single(await verify.FolderManagerReports.ToListAsync()).Id);
	}

	[Fact]
	public async Task RunAsync_BuildsOnTheEarlierReport()
	{
		await SeedProviderAsync();
		var folderId = await SeedFolderAsync("Shop");
		await SeedProjectAsync("Web", folderId, "/does/not/exist");
		var earlier = await SeedReportAsync(folderId, "Next steps\n- Fix login", DateTime.UtcNow.AddDays(-2));
		var inference = new ScriptedInferenceService(_ => new InferenceResponse { Success = true, Response = "Login is fixed." });

		var result = await CreateService(inference).RunAsync(folderId, new FolderManagerRunRequest { ContinueFromReportId = earlier.Id });

		Assert.Equal(earlier.Id, result.Report?.PreviousReportId);
		var prompt = Assert.Single(inference.Requests).Prompt.ReplaceLineEndings("\n");
		Assert.Contains("# Your earlier report, from ", prompt);
		Assert.EndsWith("Next steps\n- Fix login", prompt);

		var reports = await CreateService(inference).GetReportsAsync(folderId);
		Assert.Equal([result.Report!.Id, earlier.Id], reports.Select(r => r.Id));
	}

	[Fact]
	public async Task RunAsync_ExplainsWhyThereIsNoReport_AndSavesNothing()
	{
		var inference = new ScriptedInferenceService(_ => new InferenceResponse { Success = true, Response = "Report" });
		var emptyFolderId = await SeedFolderAsync("Empty");
		var folderId = await SeedFolderAsync("Shop");
		await SeedProjectAsync("Web", folderId, "/does/not/exist");
		var otherFolderReport = await SeedReportAsync(emptyFolderId, "Other folder", DateTime.UtcNow);
		var service = CreateService(inference);

		var missing = await service.RunAsync(Guid.NewGuid(), new FolderManagerRunRequest());
		var empty = await service.RunAsync(emptyFolderId, new FolderManagerRunRequest());
		var noProvider = await service.RunAsync(folderId, new FolderManagerRunRequest());
		await SeedProviderAsync();
		var wrongFolder = await service.RunAsync(folderId, new FolderManagerRunRequest { ContinueFromReportId = otherFolderReport.Id });
		var tooLong = await service.RunAsync(folderId, new FolderManagerRunRequest { Notes = new string('x', 2001) });

		Assert.Equal("Folder not found.", missing.Error);
		Assert.Contains("no projects yet", empty.Error);
		Assert.Contains("No inference provider replied", noProvider.Error);
		Assert.Contains("no longer exists", wrongFolder.Error);
		Assert.Contains("at most", tooLong.Error);
		Assert.Empty(inference.Requests);
		await using var verify = CreateDbContext();
		Assert.Equal(1, await verify.FolderManagerReports.CountAsync());
	}

	[Fact]
	public async Task DeleteReportAsync_KeepsReportsBuiltOnIt_AndDeletingTheFolderRemovesItsReports()
	{
		var folderId = await SeedFolderAsync("Shop");
		var first = await SeedReportAsync(folderId, "First", DateTime.UtcNow.AddDays(-1));
		var second = await SeedReportAsync(folderId, "Second", DateTime.UtcNow, previousReportId: first.Id);

		await CreateService(new ScriptedInferenceService(_ => new InferenceResponse())).DeleteReportAsync(first.Id);

		await using (var verify = CreateDbContext())
		{
			var remaining = Assert.Single(await verify.FolderManagerReports.ToListAsync());
			Assert.Equal(second.Id, remaining.Id);
			Assert.Null(remaining.PreviousReportId);

			verify.ProjectFolders.Remove(await verify.ProjectFolders.SingleAsync());
			await verify.SaveChangesAsync();
		}

		await using var afterFolderDelete = CreateDbContext();
		Assert.Empty(await afterFolderDelete.FolderManagerReports.ToListAsync());
	}

	private static Job NewJob(Guid projectId, Guid providerId, string title, JobStatus status, string? summary = null, string? error = null, string? commit = null) => new()
	{
		ProjectId = projectId,
		ProviderId = providerId,
		Title = title,
		GoalPrompt = title,
		Status = status,
		SessionSummary = summary,
		ErrorMessage = error,
		GitCommitHash = commit,
		CompletedAt = status is JobStatus.Completed or JobStatus.Failed ? DateTime.UtcNow : null
	};

	private FolderManagerService CreateService(IInferenceService inference)
		=> new(CreateDbContext(), inference, new FakeGitCommandExecutor(), NullLogger<FolderManagerService>.Instance);

	private VibeSwarmDbContext CreateDbContext() => new(_dbOptions);

	private async Task<Guid> SeedFolderAsync(string name)
	{
		await using var dbContext = CreateDbContext();
		var folder = new ProjectFolder { Id = Guid.NewGuid(), Name = name };
		dbContext.ProjectFolders.Add(folder);
		await dbContext.SaveChangesAsync();
		return folder.Id;
	}

	private async Task<Guid> SeedProjectAsync(string name, Guid? folderId, string workingPath)
	{
		await using var dbContext = CreateDbContext();
		var project = new Project { Id = Guid.NewGuid(), Name = name, WorkingPath = workingPath, FolderId = folderId };
		dbContext.Projects.Add(project);
		await dbContext.SaveChangesAsync();
		return project.Id;
	}

	private async Task<FolderManagerReport> SeedReportAsync(Guid folderId, string content, DateTime createdAt, Guid? previousReportId = null)
	{
		await using var dbContext = CreateDbContext();
		var report = new FolderManagerReport
		{
			Id = Guid.NewGuid(),
			FolderId = folderId,
			Content = content,
			CreatedAt = createdAt,
			PreviousReportId = previousReportId
		};
		dbContext.FolderManagerReports.Add(report);
		await dbContext.SaveChangesAsync();
		return report;
	}

	private async Task SeedProviderAsync()
	{
		await using var dbContext = CreateDbContext();
		var provider = new InferenceProvider { Name = "Desktop", Endpoint = "http://desktop:11434", IsEnabled = true };
		provider.Models.Add(new InferenceModel { ModelId = "qwen3", TaskType = InferenceTaskModels.Default, IsDefault = true });
		dbContext.InferenceProviders.Add(provider);
		await dbContext.SaveChangesAsync();
	}

	private sealed class FakeGitCommandExecutor : IGitCommandExecutor
	{
		public Task<GitCommandResult> ExecuteAsync(string arguments, string workingDirectory, CancellationToken cancellationToken = default, int timeoutSeconds = 30)
		{
			var output = arguments.StartsWith("status", StringComparison.Ordinal)
				? "## main...origin/main [ahead 1]\n M src/app.cs\n?? notes.txt\n"
				: arguments.StartsWith("log", StringComparison.Ordinal)
					? "abc1234\t2026-10-12\tFix the login form\n"
					: string.Empty;
			return Task.FromResult(new GitCommandResult { ExitCode = 0, Output = output });
		}

		public Task<GitCommandResult> ExecuteRawAsync(string command, string arguments, string workingDirectory, CancellationToken cancellationToken = default, int timeoutSeconds = 30)
			=> throw new NotSupportedException();
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

public sealed class FolderManagerTabTests
{
	[Fact]
	public void ShowsTheNewestReportOpen_AndARunBuildsOnItWithTheNotes()
	{
		var folderId = Guid.NewGuid();
		var older = new FolderManagerReport { Id = Guid.NewGuid(), FolderId = folderId, Content = "Older report body", CreatedAt = DateTime.UtcNow.AddDays(-3) };
		var newest = new FolderManagerReport { Id = Guid.NewGuid(), FolderId = folderId, Content = "Newest report body", CreatedAt = DateTime.UtcNow.AddDays(-1), ModelId = "qwen3", ProviderName = "Desktop" };
		var manager = new FakeFolderManagerService([newest, older])
		{
			RunResult = new FolderManagerRunResult
			{
				Report = new FolderManagerReport { Id = Guid.NewGuid(), FolderId = folderId, Content = "Fresh report body", CreatedAt = DateTime.UtcNow }
			}
		};
		using var context = CreateContext(manager, WithDefaultModel());

		var tab = context.Render<FolderManagerTab>(parameters => parameters
			.Add(component => component.FolderId, folderId)
			.Add(component => component.ProjectCount, 2));

		Assert.Contains("Newest report body", tab.Markup);
		Assert.DoesNotContain("Older report body", tab.Markup);
		Assert.Contains("Uses qwen3 on Desktop", tab.Find("#folder-manager-model").TextContent);
		Assert.Equal(newest.Id.ToString(), tab.Find("#folder-manager-continue").GetAttribute("value"));

		tab.Find("#folder-manager-notes").Input("Ready to release?");
		tab.FindAll("button").Single(button => button.TextContent.Contains("Run manager")).Click();

		var request = Assert.Single(manager.RunRequests);
		Assert.Equal(folderId, request.FolderId);
		Assert.Equal("Ready to release?", request.Request.Notes);
		Assert.Equal(newest.Id, request.Request.ContinueFromReportId);
		tab.WaitForAssertion(() => Assert.Contains("Fresh report body", tab.Markup));
		Assert.Equal(manager.RunResult.Report!.Id.ToString(), tab.Find("#folder-manager-continue").GetAttribute("value"));
	}

	[Fact]
	public void StartingFresh_SendsNoEarlierReport_AndARunErrorIsShown()
	{
		var folderId = Guid.NewGuid();
		var earlier = new FolderManagerReport { Id = Guid.NewGuid(), FolderId = folderId, Content = "Earlier", CreatedAt = DateTime.UtcNow.AddDays(-1) };
		var manager = new FakeFolderManagerService([earlier])
		{
			RunResult = new FolderManagerRunResult { Error = "No inference provider replied." }
		};
		using var context = CreateContext(manager, WithDefaultModel());
		var tab = context.Render<FolderManagerTab>(parameters => parameters
			.Add(component => component.FolderId, folderId)
			.Add(component => component.ProjectCount, 1));

		tab.Find("#folder-manager-continue").Change(string.Empty);
		tab.FindAll("button").Single(button => button.TextContent.Contains("Run manager")).Click();

		Assert.Null(Assert.Single(manager.RunRequests).Request.ContinueFromReportId);
		tab.WaitForAssertion(() => Assert.Contains("No inference provider replied.", tab.Find(".alert").TextContent));
	}

	[Fact]
	public void WithoutAnInferenceModel_ExplainsWhereToAddOne_AndCannotRun()
	{
		using var context = CreateContext(new FakeFolderManagerService([]), []);

		var tab = context.Render<FolderManagerTab>(parameters => parameters
			.Add(component => component.FolderId, Guid.NewGuid())
			.Add(component => component.ProjectCount, 1));

		Assert.Contains("No inference model is set up", tab.Find(".alert").TextContent);
		Assert.True(tab.FindAll("button").Single(button => button.TextContent.Contains("Run manager")).HasAttribute("disabled"));
		Assert.Contains("No reports yet", tab.Markup);
	}

	[Fact]
	public void FolderPage_ListsOnlyItsProjects_AndOpensTheManagerTab()
	{
		var folder = new ProjectFolder { Id = Guid.NewGuid(), Name = "Shop" };
		var projects = new FakeProjectService(folder,
			new Project { Id = Guid.NewGuid(), Name = "Web", FolderId = folder.Id },
			new Project { Id = Guid.NewGuid(), Name = "Elsewhere" });
		using var context = CreateContext(new FakeFolderManagerService([]), WithDefaultModel());
		context.Services.AddSingleton<IProjectService>(projects);
		context.Services.AddSingleton<IJobService>(new FakeJobService());

		var page = context.Render<ProjectFolderDetail>(parameters => parameters.Add(component => component.FolderId, folder.Id));

		Assert.Equal("Shop", page.Find("h1").TextContent);
		Assert.Contains("Web", page.Find(".list-group").TextContent);
		Assert.DoesNotContain("Elsewhere", page.Markup);

		page.FindAll("button[role=tab]").Single(button => button.TextContent == "Manager").Click();

		Assert.NotNull(page.Find("#folder-manager-notes"));
	}

	private static List<InferenceProvider> WithDefaultModel()
	{
		var provider = new InferenceProvider { Id = Guid.NewGuid(), Name = "Desktop", IsEnabled = true };
		provider.Models.Add(new InferenceModel { ModelId = "qwen3", TaskType = InferenceTaskModels.Default, IsDefault = true, IsAvailable = true });
		return [provider];
	}

	private static BunitContext CreateContext(FakeFolderManagerService manager, List<InferenceProvider> providers)
	{
		var context = new BunitContext();
		context.JSInterop.Mode = JSRuntimeMode.Loose;
		context.Services.AddLogging();
		context.Services.AddSingleton<IFolderManagerService>(manager);
		context.Services.AddSingleton<IInferenceProviderService>(new FakeInferenceProviderService(providers));
		context.Services.AddSingleton<NotificationService>();
		return context;
	}

	private sealed class FakeFolderManagerService(List<FolderManagerReport> reports) : IFolderManagerService
	{
		public FolderManagerRunResult RunResult { get; set; } = new();
		public List<(Guid FolderId, FolderManagerRunRequest Request)> RunRequests { get; } = [];

		public Task<IReadOnlyList<FolderManagerReport>> GetReportsAsync(Guid folderId, CancellationToken cancellationToken = default)
			=> Task.FromResult<IReadOnlyList<FolderManagerReport>>(reports);

		public Task<FolderManagerRunResult> RunAsync(Guid folderId, FolderManagerRunRequest request, CancellationToken cancellationToken = default)
		{
			RunRequests.Add((folderId, request));
			return Task.FromResult(RunResult);
		}

		public Task DeleteReportAsync(Guid reportId, CancellationToken cancellationToken = default) => Task.CompletedTask;
	}

	private sealed class FakeInferenceProviderService(List<InferenceProvider> providers) : FakeInferenceProviderServiceBase
	{
		public override Task<IEnumerable<InferenceProvider>> GetAllAsync(CancellationToken ct = default)
			=> Task.FromResult<IEnumerable<InferenceProvider>>(providers);
	}

	private sealed class FakeProjectService(ProjectFolder folder, params Project[] projects) : FakeProjectServiceBase
	{
		public override Task<IEnumerable<ProjectFolder>> GetFoldersAsync(CancellationToken cancellationToken = default)
			=> Task.FromResult<IEnumerable<ProjectFolder>>([folder]);

		public override Task<IEnumerable<ProjectWithStats>> GetAllWithStatsAsync(CancellationToken cancellationToken = default)
			=> Task.FromResult(projects.Select(project => new ProjectWithStats { Project = project }));
	}

	private sealed class FakeJobService : FakeJobServiceBase
	{
		public override Task<IEnumerable<JobSummary>> GetActiveJobsAsync(CancellationToken cancellationToken = default)
			=> Task.FromResult<IEnumerable<JobSummary>>([]);
	}
}
