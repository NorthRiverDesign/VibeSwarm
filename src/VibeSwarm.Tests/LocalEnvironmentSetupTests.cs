using System.IO;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class LocalEnvironmentSetupTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly DbContextOptions<VibeSwarmDbContext> _dbOptions;
	private readonly string _tempDirectory;

	public LocalEnvironmentSetupTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();
		_dbOptions = new DbContextOptionsBuilder<VibeSwarmDbContext>()
			.UseSqlite(_connection)
			.Options;

		_tempDirectory = Path.Combine(Path.GetTempPath(), $"vibeswarm-local-setup-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_tempDirectory);

		using var dbContext = CreateDbContext();
		dbContext.Database.EnsureCreated();
	}

	[Fact]
	public void CreateJob_TagsJobAsLocalSetup_WithShortGoalAndTitle()
	{
		var projectId = Guid.NewGuid();
		var providerId = Guid.NewGuid();

		var job = LocalEnvironmentSetup.CreateJob(projectId, providerId, "model-a", "main");

		Assert.Equal(projectId, job.ProjectId);
		Assert.Equal(providerId, job.ProviderId);
		Assert.Equal("model-a", job.ModelUsed);
		Assert.Equal("main", job.Branch);
		Assert.Equal(LocalEnvironmentSetup.JobTitle, job.Title);
		Assert.True(LocalEnvironmentSetup.IsSetupJob(job));
		Assert.InRange(job.GoalPrompt.Length, 1, 2000);
	}

	[Theory]
	[InlineData(null, false)]
	[InlineData("", false)]
	[InlineData("other", false)]
	[InlineData("local-environment-setup", true)]
	[InlineData("urgent, Local-Environment-Setup", true)]
	public void IsSetupJob_MatchesTagInCommaSeparatedList(string? tags, bool expected)
	{
		Assert.Equal(expected, LocalEnvironmentSetup.IsSetupJob(new Job { Tags = tags }));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("not json")]
	public void ParseResult_ReturnsNullForUnreadableFile(string? json)
	{
		Assert.Null(LocalEnvironmentSetup.ParseResult(json));
	}

	[Fact]
	public void ApplyResult_AddsPrimaryLocalWebEnvironment_WhenProjectHasNone()
	{
		var project = new Project { Id = Guid.NewGuid() };

		var environment = LocalEnvironmentSetup.ApplyResult(project, new LocalEnvironmentSetupResult
		{
			Url = " http://localhost:5173 ",
			StartCommand = "npm run dev",
			Notes = "Mailpit UI at http://localhost:8025"
		});

		Assert.NotNull(environment);
		Assert.Same(environment, Assert.Single(project.Environments));
		Assert.Equal(project.Id, environment.ProjectId);
		Assert.Equal("Local", environment.Name);
		Assert.Equal("http://localhost:5173", environment.Url);
		Assert.Equal(EnvironmentType.Web, environment.Type);
		Assert.Equal(EnvironmentStage.Local, environment.Stage);
		Assert.True(environment.IsEnabled);
		Assert.True(environment.IsPrimary);
		Assert.Equal("Start: npm run dev\nMailpit UI at http://localhost:8025", environment.Description);
		Assert.Null(environment.Username);
	}

	[Fact]
	public void ApplyResult_UpdatesExistingLocalEnvironment_InsteadOfAddingAnother()
	{
		var existing = new ProjectEnvironment
		{
			Id = Guid.NewGuid(),
			Name = "Dev box",
			Url = "http://localhost:3000",
			Stage = EnvironmentStage.Local,
			Type = EnvironmentType.Other,
			Description = "Old notes",
			IsEnabled = false
		};
		var production = new ProjectEnvironment
		{
			Id = Guid.NewGuid(),
			Name = "Production",
			Url = "https://example.com",
			IsPrimary = true,
			SortOrder = 1
		};
		var project = new Project { Environments = [production, existing] };

		var environment = LocalEnvironmentSetup.ApplyResult(project, new LocalEnvironmentSetupResult
		{
			Url = "http://localhost:8080",
			Username = "admin@example.test",
			Password = "local-secret"
		});

		Assert.Same(existing, environment);
		Assert.Equal(2, project.Environments.Count);
		Assert.Equal("http://localhost:8080", existing.Url);
		Assert.Equal(EnvironmentType.Web, existing.Type);
		Assert.True(existing.IsEnabled);
		Assert.False(existing.IsPrimary);
		Assert.Equal("Old notes", existing.Description);
		Assert.Equal("admin@example.test", existing.Username);
		Assert.Equal("local-secret", existing.Password);
	}

	[Fact]
	public void ApplyResult_ReusesEnvironmentNamedLocal_SoNamesStayUnique()
	{
		var named = new ProjectEnvironment { Id = Guid.NewGuid(), Name = "local", Url = "https://dev.example.com", Stage = EnvironmentStage.Development };
		var project = new Project { Environments = [named] };

		var environment = LocalEnvironmentSetup.ApplyResult(project, new LocalEnvironmentSetupResult { Url = "http://127.0.0.1:8000" });

		Assert.Same(named, environment);
		Assert.Single(project.Environments);
		Assert.Equal(EnvironmentStage.Local, named.Stage);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("localhost:3000")]
	[InlineData("ftp://localhost")]
	[InlineData("file:///tmp/app")]
	public void ApplyResult_IgnoresResultWithoutHttpUrl(string? url)
	{
		var project = new Project();

		var environment = LocalEnvironmentSetup.ApplyResult(project, new LocalEnvironmentSetupResult { Url = url, StartCommand = "make run" });

		Assert.Null(environment);
		Assert.Empty(project.Environments);
	}

	[Fact]
	public void BuildSystemPromptRules_DropsCodeChangeRequirement_ForLocalSetup()
	{
		var project = new Project { Name = "App", WorkingPath = "/tmp/app" };
		const string codeChangeRule = "The deliverable is a code change.";

		Assert.Contains(codeChangeRule, PromptBuilder.BuildSystemPromptRules(project));
		Assert.DoesNotContain(codeChangeRule, PromptBuilder.BuildSystemPromptRules(project, requireCodeChange: false));
	}

	[Fact]
	public void BuildLocalEnvironmentSetupRules_PointsAtResultFile_AndCoversLocalServices()
	{
		var rules = PromptBuilder.BuildLocalEnvironmentSetupRules("/work/app/.vibeswarm/local-environment.json");

		Assert.StartsWith("LOCAL ENVIRONMENT SETUP:", rules);
		Assert.Contains("write /work/app/.vibeswarm/local-environment.json as JSON", rules);
		Assert.Contains("\"startCommand\"", rules);
		Assert.Contains("Database:", rules);
		Assert.Contains("Email:", rules);
		Assert.Contains(".git/info/exclude", rules);
	}

	[Fact]
	public void PrepareResultFile_RemovesResultLeftByEarlierRun()
	{
		var stalePath = LocalEnvironmentSetupService.GetResultFilePath(_tempDirectory);
		Directory.CreateDirectory(Path.GetDirectoryName(stalePath)!);
		File.WriteAllText(stalePath, "{\"url\":\"http://localhost:1\"}");

		var resultFilePath = LocalEnvironmentSetupService.PrepareResultFile(_tempDirectory);

		Assert.Equal(stalePath, resultFilePath);
		Assert.False(File.Exists(resultFilePath));
		Assert.True(Directory.Exists(Path.GetDirectoryName(resultFilePath)));
	}

	[Fact]
	public async Task ApplyResultAsync_SavesLocalEnvironmentWithEncryptedLogin_AndDeletesResultFile()
	{
		var projectId = await SeedProjectAsync();
		var resultFilePath = await WriteResultAsync("""
			{
				"url": "http://localhost:4000",
				"startCommand": "bin/dev",
				"notes": "Postgres db app_local; Mailpit on :8025",
				"username": "admin@example.test",
				"password": "local-secret"
			}
			""");

		await using (var dbContext = CreateDbContext())
		{
			var environment = await CreateService(dbContext).ApplyResultAsync(projectId, _tempDirectory);
			Assert.NotNull(environment);
		}

		Assert.False(File.Exists(resultFilePath));

		await using var verifyContext = CreateDbContext();
		var stored = await verifyContext.ProjectEnvironments.AsNoTracking().SingleAsync();
		Assert.Equal(projectId, stored.ProjectId);
		Assert.Equal("Local", stored.Name);
		Assert.Equal("http://localhost:4000", stored.Url);
		Assert.Equal(EnvironmentStage.Local, stored.Stage);
		Assert.Equal(EnvironmentType.Web, stored.Type);
		Assert.Equal("Start: bin/dev\nPostgres db app_local; Mailpit on :8025", stored.Description);
		Assert.False(string.IsNullOrEmpty(stored.UsernameCiphertext));
		Assert.False(string.IsNullOrEmpty(stored.PasswordCiphertext));
		Assert.DoesNotContain("local-secret", stored.PasswordCiphertext);

		var project = await verifyContext.Projects.Include(item => item.Environments).AsNoTracking().SingleAsync();
		CreateCredentialService().PopulateForExecution(project);
		var environmentForJobs = Assert.Single(project.Environments);
		Assert.Equal("admin@example.test", environmentForJobs.Username);
		Assert.Equal("local-secret", environmentForJobs.Password);
	}

	[Fact]
	public async Task ApplyResultAsync_SecondRunUpdatesSameEnvironment_AndKeepsSavedLogin()
	{
		var projectId = await SeedProjectAsync();

		await WriteResultAsync("""{"url":"http://localhost:4000","username":"admin@example.test","password":"local-secret"}""");
		await using (var dbContext = CreateDbContext())
		{
			await CreateService(dbContext).ApplyResultAsync(projectId, _tempDirectory);
		}

		await WriteResultAsync("""{"url":"http://localhost:4100","startCommand":"bin/dev"}""");
		await using (var dbContext = CreateDbContext())
		{
			await CreateService(dbContext).ApplyResultAsync(projectId, _tempDirectory);
		}

		await using var verifyContext = CreateDbContext();
		var project = await verifyContext.Projects.Include(item => item.Environments).AsNoTracking().SingleAsync();
		CreateCredentialService().PopulateForExecution(project);
		var environment = Assert.Single(project.Environments);
		Assert.Equal("http://localhost:4100", environment.Url);
		Assert.Equal("Start: bin/dev", environment.Description);
		Assert.Equal("admin@example.test", environment.Username);
		Assert.Equal("local-secret", environment.Password);
	}

	[Fact]
	public async Task ApplyResultAsync_WithoutUsableResult_ChangesNothing()
	{
		var projectId = await SeedProjectAsync();

		await using (var dbContext = CreateDbContext())
		{
			Assert.Null(await CreateService(dbContext).ApplyResultAsync(projectId, _tempDirectory));
		}

		var resultFilePath = await WriteResultAsync("""{"notes":"No web UI; run with ./cli"}""");
		await using (var dbContext = CreateDbContext())
		{
			Assert.Null(await CreateService(dbContext).ApplyResultAsync(projectId, _tempDirectory));
		}

		Assert.False(File.Exists(resultFilePath));
		await using var verifyContext = CreateDbContext();
		Assert.Empty(await verifyContext.ProjectEnvironments.ToListAsync());
	}

	private async Task<Guid> SeedProjectAsync()
	{
		await using var dbContext = CreateDbContext();
		var project = new Project { Id = Guid.NewGuid(), Name = "Local App", WorkingPath = _tempDirectory };
		dbContext.Projects.Add(project);
		await dbContext.SaveChangesAsync();
		return project.Id;
	}

	private async Task<string> WriteResultAsync(string json)
	{
		var resultFilePath = LocalEnvironmentSetupService.PrepareResultFile(_tempDirectory);
		await File.WriteAllTextAsync(resultFilePath, json);
		return resultFilePath;
	}

	private LocalEnvironmentSetupService CreateService(VibeSwarmDbContext dbContext) =>
		new(dbContext, CreateCredentialService(), NullLogger<LocalEnvironmentSetupService>.Instance);

	private ProjectEnvironmentCredentialService CreateCredentialService() =>
		new(DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_tempDirectory, "keys"))));

	private VibeSwarmDbContext CreateDbContext() => new(_dbOptions);

	public void Dispose()
	{
		_connection.Dispose();
		if (Directory.Exists(_tempDirectory))
		{
			Directory.Delete(_tempDirectory, recursive: true);
		}
	}
}
