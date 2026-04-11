using System.IO;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.VersionControl;
using VibeSwarm.Shared.VersionControl.Models;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class ProjectEnvironmentFeatureTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly DbContextOptions<VibeSwarmDbContext> _dbOptions;
	private readonly string _dataProtectionDirectory;

	public ProjectEnvironmentFeatureTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();

		_dbOptions = new DbContextOptionsBuilder<VibeSwarmDbContext>()
			.UseSqlite(_connection)
			.Options;

		_dataProtectionDirectory = Path.Combine(Path.GetTempPath(), $"vibeswarm-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_dataProtectionDirectory);

		using var dbContext = CreateDbContext();
		dbContext.Database.EnsureCreated();
	}

	[Fact]
	public async Task CreateAsync_EncryptsWebCredentials_AndHydratesClientFields()
	{
		await using var dbContext = CreateDbContext();
		var service = CreateProjectService(dbContext);

		var created = await service.CreateAsync(new Project
		{
			Name = "Environment Project",
			WorkingPath = "/tmp/environment-project",
			Environments =
			[
				new ProjectEnvironment
				{
					Name = "Staging",
					Type = EnvironmentType.Web,
					Url = "https://staging.example.com",
					Username = "tester@example.com",
					Password = "Sup3rSecret!"
				}
			]
		});

		var stored = await dbContext.ProjectEnvironments.AsNoTracking().SingleAsync();
		Assert.NotEqual("tester@example.com", stored.UsernameCiphertext);
		Assert.NotEqual("Sup3rSecret!", stored.PasswordCiphertext);

		var environment = Assert.Single(created.Environments);
		Assert.Equal("tester@example.com", environment.Username);
		Assert.True(environment.HasPassword);
		Assert.Null(environment.Password);
	}

	[Fact]
	public async Task UpdateAsync_BlankPasswordPreservesStoredSecret_AndClearPasswordRemovesIt()
	{
		await using var dbContext = CreateDbContext();
		var service = CreateProjectService(dbContext);

		var created = await service.CreateAsync(new Project
		{
			Name = "Environment Project",
			WorkingPath = "/tmp/environment-project",
			Environments =
			[
				new ProjectEnvironment
				{
					Name = "Preview",
					Type = EnvironmentType.Web,
					Url = "https://preview.example.com",
					Username = "preview-user",
					Password = "InitialPassword!"
				}
			]
		});

		var storedBefore = await dbContext.ProjectEnvironments.AsNoTracking().SingleAsync();
		var originalPasswordCiphertext = storedBefore.PasswordCiphertext;

		var environment = Assert.Single(created.Environments);
		environment.Password = null;
		environment.ClearPassword = false;
		created.Environments = [environment];
		await service.UpdateAsync(created);

		var storedAfterPreserve = await dbContext.ProjectEnvironments.AsNoTracking().SingleAsync();
		Assert.Equal(originalPasswordCiphertext, storedAfterPreserve.PasswordCiphertext);

		environment.ClearPassword = true;
		created.Environments = [environment];
		var updated = await service.UpdateAsync(created);

		var storedAfterClear = await dbContext.ProjectEnvironments.AsNoTracking().SingleAsync();
		Assert.Null(storedAfterClear.PasswordCiphertext);
		Assert.False(Assert.Single(updated.Environments).HasPassword);
	}

	[Fact]
	public async Task UpdateAsync_UsernameCanBeClearedWhileKeepingSavedPassword()
	{
		await using var dbContext = CreateDbContext();
		var service = CreateProjectService(dbContext);

		var created = await service.CreateAsync(new Project
		{
			Name = "Environment Project",
			WorkingPath = "/tmp/environment-project",
			Environments =
			[
				new ProjectEnvironment
				{
					Name = "Preview",
					Type = EnvironmentType.Web,
					Url = "https://preview.example.com",
					Username = "preview-user",
					Password = "InitialPassword!"
				}
			]
		});

		var environment = Assert.Single(created.Environments);
		environment.Username = null;
		environment.Password = null;
		environment.ClearPassword = false;
		created.Environments = [environment];

		var updated = await service.UpdateAsync(created);
		var stored = await dbContext.ProjectEnvironments.AsNoTracking().SingleAsync();

		Assert.Null(stored.UsernameCiphertext);
		Assert.NotNull(stored.PasswordCiphertext);
		Assert.Null(Assert.Single(updated.Environments).Username);
		Assert.True(Assert.Single(updated.Environments).HasPassword);
	}

	[Fact]
	public async Task CreateAsync_WebEnvironment_AllowsPasswordWithoutUsername()
	{
		await using var dbContext = CreateDbContext();
		var service = CreateProjectService(dbContext);

		var created = await service.CreateAsync(new Project
		{
			Name = "Environment Project",
			WorkingPath = "/tmp/environment-project",
			Environments =
			[
				new ProjectEnvironment
				{
					Name = "Preview",
					Type = EnvironmentType.Web,
					Url = "https://preview.example.com",
					Password = "InitialPassword!"
				}
			]
		});

		var stored = await dbContext.ProjectEnvironments.AsNoTracking().SingleAsync();

		Assert.Null(stored.UsernameCiphertext);
		Assert.NotNull(stored.PasswordCiphertext);
		Assert.True(Assert.Single(created.Environments).HasPassword);
	}

	[Fact]
	public void BuildStructuredPrompt_IncludesEnvironmentContextAndCredentials()
	{
		var job = new Job
		{
			GoalPrompt = "Verify the deployed app works.",
			Project = new Project
			{
				Name = "Web App",
				Description = "A deployed app",
				Environments =
				[
					new ProjectEnvironment
					{
						Name = "Staging",
						Type = EnvironmentType.Web,
						Stage = EnvironmentStage.Development,
						Url = "https://staging.example.com",
						Description = "Use the seeded admin account.",
						IsPrimary = true,
						IsEnabled = true,
						Username = "admin@example.com",
						Password = "StagingPassword!"
					},
					new ProjectEnvironment
					{
						Name = "Releases",
						Type = EnvironmentType.Release,
						Stage = EnvironmentStage.Production,
						Url = "https://github.com/octo/repo/releases",
						IsEnabled = true
					},
					new ProjectEnvironment
					{
						Name = "Local App",
						Type = EnvironmentType.Web,
						Stage = EnvironmentStage.Local,
						Url = "http://localhost:5000",
						IsEnabled = true
					}
				]
			}
		};

		var prompt = PromptBuilder.BuildStructuredPrompt(job, true);

		Assert.Contains("<environments>", prompt);
		Assert.Contains("Prefer these URLs instead of assuming localhost.", prompt);
		Assert.Contains("Production environments are live/stable targets.", prompt);
		Assert.Contains("Development environments allow normal testing, iterative changes, and redeploys", prompt);
		Assert.Contains("Local environments assume immediate feedback.", prompt);
		Assert.Contains("Primary Web [Development]: Staging", prompt);
		Assert.Contains("https://staging.example.com", prompt);
		Assert.Contains("Login: Username=admin@example.com, Password=StagingPassword!", prompt);
		Assert.Contains("Web [Local]: Local App", prompt);
		Assert.Contains("http://localhost:5000", prompt);
		Assert.Contains("https://github.com/octo/repo/releases", prompt);
	}

	[Fact]
	public void BuildStructuredPrompt_IncludesPartialEnvironmentCredentials()
	{
		var job = new Job
		{
			GoalPrompt = "Verify the deployed app works.",
			Project = new Project
			{
				Name = "Web App",
				Environments =
				[
					new ProjectEnvironment
					{
						Name = "Username Only",
						Type = EnvironmentType.Web,
						Stage = EnvironmentStage.Development,
						Url = "https://username-only.example.com",
						IsEnabled = true,
						Username = "admin@example.com"
					},
					new ProjectEnvironment
					{
						Name = "Password Only",
						Type = EnvironmentType.Web,
						Stage = EnvironmentStage.Development,
						Url = "https://password-only.example.com",
						IsEnabled = true,
						Password = "StagingPassword!"
					}
				]
			}
		};

		var prompt = PromptBuilder.BuildStructuredPrompt(job, true);

		Assert.Contains("Login: Username=admin@example.com", prompt);
		Assert.Contains("Login: Password=StagingPassword!", prompt);
	}

	[Fact]
	public void BuildSystemPromptRules_IncludesEnvironmentStageGuidance()
	{
		var rules = PromptBuilder.BuildSystemPromptRules(new Project
		{
			Name = "Web App",
			WorkingPath = "/tmp/web-app",
			Environments =
			[
				new ProjectEnvironment
				{
					Name = "Production",
					Type = EnvironmentType.Web,
					Stage = EnvironmentStage.Production,
					Url = "https://app.example.com",
					IsEnabled = true
				},
				new ProjectEnvironment
				{
					Name = "Local",
					Type = EnvironmentType.Web,
					Stage = EnvironmentStage.Local,
					Url = "http://localhost:5000",
					IsEnabled = true
				}
			]
		});

		Assert.NotNull(rules);
		Assert.Contains("ENVIRONMENT SAFETY:", rules);
		Assert.Contains("Only make direct environment changes or redeploy them when the task explicitly asks for it", rules);
		Assert.Contains("rebuild, restart services, redeploy, reseed data, or wipe the local database", rules);
	}

	[Fact]
	public void BuildSystemPromptRules_IncludesCredentialUsageGuidance()
	{
		var rules = PromptBuilder.BuildSystemPromptRules(new Project
		{
			Name = "Web App",
			WorkingPath = "/tmp/web-app",
			Environments =
			[
				new ProjectEnvironment
				{
					Name = "Staging",
					Type = EnvironmentType.Web,
					Stage = EnvironmentStage.Development,
					Url = "https://staging.example.com",
					IsEnabled = true,
					Username = "admin@example.com",
					Password = "StagingPassword!"
				}
			]
		});

		Assert.NotNull(rules);
		Assert.Contains("ENVIRONMENT AUTHENTICATION:", rules);
		Assert.Contains("use those exact values for browser automation", rules);
		Assert.Contains("test@test.com", rules);
	}

	[Fact]
	public async Task GenerateMcpConfigJsonAsync_AddsPlaywrightServerWhenWebEnvironmentExists()
	{
		var repoMapSkill = new Skill { Id = Guid.NewGuid(), Name = "repo-map", Description = "Repository navigation help.", Content = "Skill content", IsEnabled = true };
		var unrelatedSkill = new Skill { Id = Guid.NewGuid(), Name = "other-skill", Description = "Should not be injected.", Content = "Other content", IsEnabled = true };
		var service = new McpConfigService(new FakeSkillService(
			repoMapSkill,
			unrelatedSkill));

		var json = await service.GenerateMcpConfigJsonAsync(new Project
		{
			Name = "Web App",
			WorkingPath = "/tmp/web-app",
			TeamAssignments =
			[
				new ProjectTeamRole
				{
					TeamRoleId = Guid.NewGuid(),
					IsEnabled = true,
					TeamRole = new TeamRole
					{
						Id = Guid.NewGuid(),
						Name = "Code Reviewer",
						IsEnabled = true,
						SkillLinks =
						[
							new TeamRoleSkill
							{
								SkillId = repoMapSkill.Id,
								Skill = repoMapSkill
							}
						]
					}
				}
			],
			Environments =
			[
				new ProjectEnvironment
				{
					Name = "Production",
					Type = EnvironmentType.Web,
					Url = "https://app.example.com",
					Username = "admin@example.com",
					Password = "ProdPassword!",
					IsEnabled = true
				}
			]
		});

		Assert.NotNull(json);
		Assert.Contains("\"playwright\"", json);
		Assert.Contains("@playwright/mcp@latest", json);
		Assert.Contains("\"repo-map\"", json);
		Assert.DoesNotContain("\"other-skill\"", json);
		Assert.Contains("\"APP_URL\"", json);
		Assert.Contains("\"admin@example.com\"", json);
	}

	[Fact]
	public async Task GenerateMcpConfigFileAsync_UsesOpenCodeShapeForOpenCodeProvider()
	{
		var service = new McpConfigService(new FakeSkillService());

		var filePath = await service.GenerateMcpConfigFileAsync(
			ProviderType.OpenCode,
			new Project
			{
				Name = "Web App",
				WorkingPath = "/tmp/web-app",
				Environments =
				[
					new ProjectEnvironment
					{
						Name = "Production",
						Type = EnvironmentType.Web,
						Url = "https://app.example.com",
						IsEnabled = true
					}
				]
			});

		Assert.NotNull(filePath);
		var json = await File.ReadAllTextAsync(filePath!);
		Assert.Contains("\"mcp\"", json);
		Assert.Contains("@playwright/mcp@latest", json);
		Assert.Contains("\"PLAYWRIGHT_BROWSERS_PATH\"", json);
		service.CleanupMcpConfigFiles();
	}

	[Fact]
	public async Task GenerateExecutionResourcesAsync_IncludesProjectMcpServersFromDotMcpJson()
	{
		var service = new McpConfigService(new FakeSkillService());
		var workingDirectory = Path.Combine(Path.GetTempPath(), $"vibeswarm-project-mcp-{Guid.NewGuid():N}");
		Directory.CreateDirectory(workingDirectory);
		McpExecutionResources? resources = null;

		try
		{
			await WriteProjectMcpConfigAsync(workingDirectory, """
				{
				  "mcpServers": {
				    "laravel-boost": {
				      "command": "php",
				      "args": ["artisan", "boost:mcp"]
				    }
				  }
				}
				""");

			resources = await service.GenerateExecutionResourcesAsync(
				ProviderType.Claude,
				new Project
				{
					Name = "Laravel App",
					WorkingPath = workingDirectory
				},
				workingDirectory);

			Assert.NotNull(resources);
			Assert.NotNull(resources!.ConfigFilePath);
			using var document = JsonDocument.Parse(await File.ReadAllTextAsync(resources.ConfigFilePath!));
			var server = document.RootElement
				.GetProperty("mcpServers")
				.GetProperty("laravel-boost");

			Assert.Equal("php", server.GetProperty("command").GetString());
			Assert.Equal(
				["artisan", "boost:mcp"],
				server.GetProperty("args").EnumerateArray().Select(value => value.GetString()).ToList());
		}
		finally
		{
			service.CleanupExecutionResources(resources);
			if (Directory.Exists(workingDirectory))
			{
				Directory.Delete(workingDirectory, recursive: true);
			}
		}
	}

	[Fact]
	public async Task GenerateExecutionResourcesAsync_ForOpenCode_MergesProjectMcpServersWithPlaywright()
	{
		var service = new McpConfigService(new FakeSkillService());
		var workingDirectory = Path.Combine(Path.GetTempPath(), $"vibeswarm-project-opencode-mcp-{Guid.NewGuid():N}");
		Directory.CreateDirectory(workingDirectory);
		McpExecutionResources? resources = null;

		try
		{
			await WriteProjectMcpConfigAsync(workingDirectory, """
				{
				  "mcpServers": {
				    "laravel-boost": {
				      "command": "php",
				      "args": ["artisan", "boost:mcp"]
				    }
				  }
				}
				""");

			resources = await service.GenerateExecutionResourcesAsync(
				ProviderType.OpenCode,
				new Project
				{
					Name = "Laravel App",
					WorkingPath = workingDirectory,
					Environments =
					[
						new ProjectEnvironment
						{
							Name = "Production",
							Type = EnvironmentType.Web,
							Url = "https://app.example.com",
							IsEnabled = true,
							IsPrimary = true
						}
					]
				},
				workingDirectory);

			Assert.NotNull(resources);
			Assert.NotNull(resources!.ConfigFilePath);
			using var document = JsonDocument.Parse(await File.ReadAllTextAsync(resources.ConfigFilePath!));
			var mcp = document.RootElement.GetProperty("mcp");

			var laravelBoost = mcp.GetProperty("laravel-boost");
			Assert.Equal("local", laravelBoost.GetProperty("type").GetString());
			Assert.Equal(
				["php", "artisan", "boost:mcp"],
				laravelBoost.GetProperty("command").EnumerateArray().Select(value => value.GetString()).ToList());

			var playwright = mcp.GetProperty("playwright");
			Assert.Equal("local", playwright.GetProperty("type").GetString());
			Assert.Equal(
				["npx", "-y", "@playwright/mcp@latest"],
				playwright.GetProperty("command").EnumerateArray().Select(value => value.GetString()).ToList());
			Assert.Equal(
				"https://app.example.com",
				playwright.GetProperty("environment").GetProperty("APP_URL").GetString());
		}
		finally
		{
			service.CleanupExecutionResources(resources);
			if (Directory.Exists(workingDirectory))
			{
				Directory.Delete(workingDirectory, recursive: true);
			}
		}
	}

	[Fact]
	public async Task GenerateExecutionResourcesAsync_IgnoresInvalidProjectMcpConfig_AndStillGeneratesManagedServers()
	{
		var service = new McpConfigService(new FakeSkillService());
		var workingDirectory = Path.Combine(Path.GetTempPath(), $"vibeswarm-project-invalid-mcp-{Guid.NewGuid():N}");
		Directory.CreateDirectory(workingDirectory);
		McpExecutionResources? resources = null;

		try
		{
			await WriteProjectMcpConfigAsync(workingDirectory, "{ not-valid-json");

			resources = await service.GenerateExecutionResourcesAsync(
				ProviderType.Claude,
				new Project
				{
					Name = "Web App",
					WorkingPath = workingDirectory,
					Environments =
					[
						new ProjectEnvironment
						{
							Name = "Production",
							Type = EnvironmentType.Web,
							Url = "https://app.example.com",
							IsEnabled = true,
							IsPrimary = true
						}
					]
				},
				workingDirectory);

			Assert.NotNull(resources);
			Assert.NotNull(resources!.ConfigFilePath);

			using var document = JsonDocument.Parse(await File.ReadAllTextAsync(resources.ConfigFilePath!));
			Assert.True(document.RootElement.GetProperty("mcpServers").TryGetProperty("playwright", out _));
		}
		finally
		{
			service.CleanupExecutionResources(resources);
			if (Directory.Exists(workingDirectory))
			{
				Directory.Delete(workingDirectory, recursive: true);
			}
		}
	}

	[Fact]
	public async Task GenerateExecutionResourcesAsync_ForCopilot_DoesNotDuplicateProjectMcpServers()
	{
		var repoMapSkill = new Skill
		{
			Id = Guid.NewGuid(),
			Name = "repo-map",
			Description = "Repository navigation help.",
			Content = "Skill content",
			IsEnabled = true
		};
		var service = new McpConfigService(new FakeSkillService(repoMapSkill));
		var workingDirectory = Path.Combine(Path.GetTempPath(), $"vibeswarm-project-copilot-mcp-{Guid.NewGuid():N}");
		Directory.CreateDirectory(workingDirectory);
		McpExecutionResources? resources = null;

		try
		{
			await WriteProjectMcpConfigAsync(workingDirectory, """
				{
				  "mcpServers": {
				    "playwright": {
				      "command": "npx",
				      "args": ["-y", "@playwright/mcp@latest"]
				    },
				    "repo-map": {
				      "command": "custom-repo-map"
				    }
				  }
				}
				""");

			resources = await service.GenerateExecutionResourcesAsync(
				ProviderType.Copilot,
				new Project
				{
					Name = "Copilot App",
					WorkingPath = workingDirectory,
					TeamAssignments =
					[
						new ProjectTeamRole
						{
							TeamRoleId = Guid.NewGuid(),
							IsEnabled = true,
							TeamRole = new TeamRole
							{
								Id = Guid.NewGuid(),
								Name = "Code Reviewer",
								IsEnabled = true,
								SkillLinks =
								[
									new TeamRoleSkill
									{
										SkillId = repoMapSkill.Id,
										Skill = repoMapSkill
									}
								]
							}
						}
					],
					Environments =
					[
						new ProjectEnvironment
						{
							Name = "Production",
							Type = EnvironmentType.Web,
							Url = "https://app.example.com",
							IsEnabled = true,
							IsPrimary = true
						}
					]
				},
				workingDirectory);

			Assert.NotNull(resources);
			Assert.NotNull(resources!.ConfigFilePath);

			using var document = JsonDocument.Parse(await File.ReadAllTextAsync(resources.ConfigFilePath!));
			var mcpServers = document.RootElement.GetProperty("mcpServers");

			Assert.False(mcpServers.TryGetProperty("playwright", out _));
			Assert.False(mcpServers.TryGetProperty("repo-map", out _));
			Assert.True(mcpServers.TryGetProperty("playwright-1", out var playwrightServer));
			Assert.True(mcpServers.TryGetProperty("repo-map-1", out var repoMapServer));
			Assert.Equal("npx", playwrightServer.GetProperty("command").GetString());
			Assert.Equal("vibeswarm-skill", repoMapServer.GetProperty("command").GetString());
			Assert.Equal(2, mcpServers.EnumerateObject().Count());
		}
		finally
		{
			service.CleanupExecutionResources(resources);
			if (Directory.Exists(workingDirectory))
			{
				Directory.Delete(workingDirectory, recursive: true);
			}
		}
	}

	[Fact]
	public async Task GenerateExecutionResourcesAsync_StoresPlaywrightArtifactsOutsideWorkingDirectory_AndCleansThemUp()
	{
		var service = new McpConfigService(new FakeSkillService());
		var workingDirectory = Path.Combine(Path.GetTempPath(), $"vibeswarm-browser-test-{Guid.NewGuid():N}");
		Directory.CreateDirectory(workingDirectory);

		try
		{
			var resources = await service.GenerateExecutionResourcesAsync(
				ProviderType.Claude,
				new Project
				{
					Name = "Web App",
					WorkingPath = workingDirectory,
					Environments =
					[
						new ProjectEnvironment
						{
							Name = "Production",
							Type = EnvironmentType.Web,
							Url = "https://app.example.com",
							Username = "admin@example.com",
							Password = "ProdPassword!",
							IsPrimary = true,
							IsEnabled = true
						},
						new ProjectEnvironment
						{
							Name = "Local Dev",
							Type = EnvironmentType.Web,
							Url = "http://localhost:5000",
							IsEnabled = true
						}
					]
				},
				workingDirectory);

			Assert.NotNull(resources);
			Assert.NotNull(resources!.ConfigFilePath);
			Assert.NotNull(resources.BrowserArtifactsDirectory);
			Assert.True(File.Exists(resources.ConfigFilePath));
			Assert.True(Directory.Exists(resources.BrowserArtifactsDirectory));

			var fullWorkingDirectory = Path.GetFullPath(workingDirectory);
			var fullArtifactsDirectory = Path.GetFullPath(resources.BrowserArtifactsDirectory!);
			Assert.False(fullArtifactsDirectory.StartsWith(fullWorkingDirectory, StringComparison.OrdinalIgnoreCase));

			using var document = JsonDocument.Parse(await File.ReadAllTextAsync(resources.ConfigFilePath));
			var playwrightServer = document.RootElement
				.GetProperty("mcpServers")
				.GetProperty("playwright");
			var environment = playwrightServer.GetProperty("env");

			Assert.Equal(Path.Combine(resources.BrowserArtifactsDirectory!, "ms-playwright"), environment.GetProperty("PLAYWRIGHT_BROWSERS_PATH").GetString());
			Assert.Equal(Path.Combine(resources.BrowserArtifactsDirectory!, "tmp"), environment.GetProperty("TMPDIR").GetString());
			Assert.Equal(Path.Combine(resources.BrowserArtifactsDirectory!, "tmp"), environment.GetProperty("TMP").GetString());
			Assert.Equal(Path.Combine(resources.BrowserArtifactsDirectory!, "tmp"), environment.GetProperty("TEMP").GetString());
			Assert.Equal(Path.Combine(resources.BrowserArtifactsDirectory!, "cache"), environment.GetProperty("XDG_CACHE_HOME").GetString());
			Assert.Equal("https://app.example.com", environment.GetProperty("APP_URL").GetString());
			Assert.Equal("admin@example.com", environment.GetProperty("APP_USERNAME").GetString());
			Assert.Equal("ProdPassword!", environment.GetProperty("APP_PASSWORD").GetString());
			Assert.Equal("https://app.example.com", environment.GetProperty("APP_PRODUCTION_URL").GetString());
			Assert.Equal("admin@example.com", environment.GetProperty("APP_PRODUCTION_USERNAME").GetString());
			Assert.Equal("ProdPassword!", environment.GetProperty("APP_PRODUCTION_PASSWORD").GetString());
			Assert.Equal("http://localhost:5000", environment.GetProperty("APP_LOCAL_DEV_URL").GetString());

			service.CleanupExecutionResources(resources);

			Assert.False(File.Exists(resources.ConfigFilePath));
			Assert.False(Directory.Exists(resources.BrowserArtifactsDirectory));
		}
		finally
		{
			if (Directory.Exists(workingDirectory))
			{
				Directory.Delete(workingDirectory, recursive: true);
			}
		}
	}

	[Fact]
	public async Task GenerateExecutionResourcesAsync_ForCopilot_CreatesBashEnvFile_AndCleansItUp()
	{
		var service = new McpConfigService(new FakeSkillService());

		var resources = await service.GenerateExecutionResourcesAsync(ProviderType.Copilot);

		if (OperatingSystem.IsWindows())
		{
			// On Windows no bash env file is created; with no MCP needs the service returns null.
			Assert.Null(resources);
			return;
		}

		Assert.NotNull(resources);
		Assert.Null(resources!.ConfigFilePath);
		Assert.Null(resources.BrowserArtifactsDirectory);
		Assert.NotNull(resources.BashEnvFilePath);
		Assert.True(File.Exists(resources.BashEnvFilePath));

		var content = await File.ReadAllTextAsync(resources.BashEnvFilePath!);
		Assert.Contains("export PATH='", content);
		Assert.Contains("command -v fdfind", content);
		Assert.Contains("fd() { command fdfind \"$@\"; }", content);

		service.CleanupExecutionResources(resources);

		Assert.False(File.Exists(resources.BashEnvFilePath));
	}

	[Fact]
	public async Task CreateAsync_MultipleEnvironments_NonPrimaryIsPrimary_False_Inserts()
	{
		// Regression: HasDefaultValue(false) on IsPrimary caused EF Core to omit
		// the column from INSERT when the value was false. The DB had no DEFAULT
		// clause, resulting in a NOT NULL constraint error (500).
		await using var dbContext = CreateDbContext();
		var service = CreateProjectService(dbContext);

		var created = await service.CreateAsync(new Project
		{
			Name = "Multi Env Project",
			WorkingPath = "/tmp/multi-env",
			Environments =
			[
				new ProjectEnvironment
				{
					Name = "Production",
					Type = EnvironmentType.Web,
					Stage = EnvironmentStage.Production,
					Url = "https://prod.example.com",
					IsPrimary = true,
					IsEnabled = true
				},
				new ProjectEnvironment
				{
					Name = "Staging",
					Type = EnvironmentType.Web,
					Stage = EnvironmentStage.Development,
					Url = "https://staging.example.com",
					IsPrimary = false,
					IsEnabled = true
				}
			]
		});

		var environments = await dbContext.ProjectEnvironments
			.AsNoTracking()
			.OrderBy(e => e.SortOrder)
			.ToListAsync();

		Assert.Equal(2, environments.Count);
		Assert.True(environments[0].IsPrimary);
		Assert.False(environments[1].IsPrimary);
		Assert.Equal(EnvironmentStage.Production, environments[0].Stage);
		Assert.Equal(EnvironmentStage.Development, environments[1].Stage);
	}

	[Fact]
	public async Task UpdateAsync_AddSecondEnvironment_NonPrimary_Succeeds()
	{
		// Regression: Adding a second environment where IsPrimary = false would
		// fail on INSERT due to missing DB DEFAULT for the IsPrimary column.
		await using var dbContext = CreateDbContext();
		var service = CreateProjectService(dbContext);

		var created = await service.CreateAsync(new Project
		{
			Name = "Single Env Project",
			WorkingPath = "/tmp/single-env",
			Environments =
			[
				new ProjectEnvironment
				{
					Name = "Production",
					Type = EnvironmentType.Web,
					Stage = EnvironmentStage.Production,
					Url = "https://prod.example.com",
					IsPrimary = true,
					IsEnabled = true
				}
			]
		});

		var env = Assert.Single(created.Environments);
		created.Environments = new List<ProjectEnvironment>
		{
			env,
			new ProjectEnvironment
			{
				Name = "Staging",
				Type = EnvironmentType.Web,
				Stage = EnvironmentStage.Local,
				Url = "https://staging.example.com",
				IsPrimary = false,
				IsEnabled = true
			}
		};

		var updated = await service.UpdateAsync(created);

		Assert.Equal(2, updated.Environments.Count);
		Assert.Single(updated.Environments, e => e.IsPrimary);
		Assert.Single(updated.Environments, e => !e.IsPrimary);
		Assert.Contains(updated.Environments, e => e.Stage == EnvironmentStage.Local);
	}

	[Fact]
	public async Task UpdateAsync_AddFirstEnvironment_SeparateContexts_Succeeds()
	{
		// Simulate the real-world flow: project created in one request,
		// environment added in a separate request (different DbContext).
		Guid projectId;

		// Request 1: Create a project with no environments
		{
			await using var ctx = CreateDbContext();
			var svc = CreateProjectService(ctx);
			var created = await svc.CreateAsync(new Project
			{
				Name = "Fresh Project",
				WorkingPath = "/tmp/fresh"
			});
			projectId = created.Id;
		}

		// Request 2: Load the project, add an environment, save
		{
			await using var ctx = CreateDbContext();
			var svc = CreateProjectService(ctx);

			// Simulates what the client does: GET project, add env, PUT project
			var project = await svc.GetByIdAsync(projectId);
			Assert.NotNull(project);

			// Simulate a JSON round-trip (client serializes properties it knows about)
			var roundTripped = new Project
			{
				Id = project!.Id,
				Name = project.Name,
				Description = project.Description,
				WorkingPath = project.WorkingPath,
				IsActive = project.IsActive,
				ProviderSelections = project.ProviderSelections
					.Select(ps => new ProjectProvider
					{
						Id = ps.Id,
						ProjectId = ps.ProjectId,
						ProviderId = ps.ProviderId,
						Priority = ps.Priority,
						IsEnabled = ps.IsEnabled,
						PreferredModelId = ps.PreferredModelId,
						CreatedAt = ps.CreatedAt,
						UpdatedAt = ps.UpdatedAt
					}).ToList(),
				Environments = new List<ProjectEnvironment>
				{
					new()
					{
						Id = Guid.NewGuid(),
						Name = "Staging",
						Type = EnvironmentType.Web,
						Stage = EnvironmentStage.Development,
						Url = "https://staging.example.com",
						IsEnabled = true,
						IsPrimary = true
					}
				}
			};

			var updated = await svc.UpdateAsync(roundTripped);

			var env = Assert.Single(updated.Environments);
			Assert.Equal("Staging", env.Name);
			Assert.True(env.IsPrimary);
			Assert.Equal(EnvironmentStage.Development, env.Stage);
		}

		// Verify the environment is persisted
		{
			await using var ctx = CreateDbContext();
			var envs = await ctx.ProjectEnvironments.AsNoTracking().ToListAsync();
			Assert.Single(envs);
		}
	}

	[Fact]
	public async Task UpdateAsync_AddFirstEnvironment_WithProviderSelections_SeparateContexts_Succeeds()
	{
		// When saving environments, the client also sends back provider selections.
		// This test verifies that both collections are handled correctly in separate contexts.
		Guid projectId;
		Guid providerId;

		// Setup: Create a provider
		{
			await using var ctx = CreateDbContext();
			var provider = new Provider
			{
				Name = "Test Provider",
				Type = ProviderType.Claude,
				IsEnabled = true
			};
			ctx.Providers.Add(provider);
			await ctx.SaveChangesAsync();
			providerId = provider.Id;
		}

		// Request 1: Create a project with a provider selection
		{
			await using var ctx = CreateDbContext();
			var svc = CreateProjectService(ctx);
			var created = await svc.CreateAsync(new Project
			{
				Name = "Project With Provider",
				WorkingPath = "/tmp/with-provider",
				ProviderSelections =
				[
					new ProjectProvider
					{
						ProviderId = providerId,
						Priority = 0,
						IsEnabled = true
					}
				]
			});
			projectId = created.Id;
			Assert.Single(created.ProviderSelections);
		}

		// Request 2: Load the project, add an environment, send back with existing provider selections
		{
			await using var ctx = CreateDbContext();
			var svc = CreateProjectService(ctx);

			var project = await svc.GetByIdAsync(projectId);
			Assert.NotNull(project);

			// Simulate client round-trip: reconstruct from JSON-like data
			var roundTripped = new Project
			{
				Id = project!.Id,
				Name = project.Name,
				Description = project.Description,
				WorkingPath = project.WorkingPath,
				IsActive = project.IsActive,
				ProviderSelections = project.ProviderSelections
					.Select(ps => new ProjectProvider
					{
						Id = ps.Id,
						ProjectId = ps.ProjectId,
						ProviderId = ps.ProviderId,
						Priority = ps.Priority,
						IsEnabled = ps.IsEnabled,
						PreferredModelId = ps.PreferredModelId,
						CreatedAt = ps.CreatedAt,
						UpdatedAt = ps.UpdatedAt
					}).ToList(),
				Environments = new List<ProjectEnvironment>
				{
					new()
					{
						Id = Guid.NewGuid(),
						Name = "Production",
						Type = EnvironmentType.Web,
						Url = "https://prod.example.com",
						IsEnabled = true,
						IsPrimary = true
					}
				}
			};

			var updated = await svc.UpdateAsync(roundTripped);

			Assert.Single(updated.Environments);
			Assert.Single(updated.ProviderSelections);
		}
	}

	[Fact]
	public void BuildJobEnvironmentVariables_ReturnsNullForProjectWithNoEnvironments()
	{
		var service = CreateCredentialService();
		var result = service.BuildJobEnvironmentVariables(new Project { Name = "Empty", WorkingPath = "/tmp" });
		Assert.Null(result);
	}

	[Fact]
	public void BuildJobEnvironmentVariables_SkipsDisabledEnvironments()
	{
		var service = CreateCredentialService();
		var project = new Project
		{
			Name = "Test",
			WorkingPath = "/tmp",
			Environments =
			[
				new ProjectEnvironment
				{
					Name = "Production",
					Url = "https://prod.example.com",
					IsEnabled = false,
					IsPrimary = true
				}
			]
		};

		var result = service.BuildJobEnvironmentVariables(project);
		Assert.Null(result);
	}

	[Fact]
	public void BuildJobEnvironmentVariables_ExposesPrimaryShortcutsAndNamedVars()
	{
		var service = CreateCredentialService();
		var project = new Project
		{
			Name = "Test",
			WorkingPath = "/tmp",
			Environments =
			[
				new ProjectEnvironment
				{
					Name = "Production",
					Type = EnvironmentType.Web,
					Url = "https://prod.example.com",
					Username = "admin",
					Password = "secret",
					IsEnabled = true,
					IsPrimary = true
				},
				new ProjectEnvironment
				{
					Name = "Local Dev",
					Type = EnvironmentType.Web,
					Url = "http://localhost:3000",
					IsEnabled = true,
					IsPrimary = false
				}
			]
		};

		var result = service.BuildJobEnvironmentVariables(project);

		Assert.NotNull(result);
		// Primary shortcuts
		Assert.Equal("https://prod.example.com", result["APP_URL"]);
		Assert.Equal("admin", result["APP_USERNAME"]);
		Assert.Equal("secret", result["APP_PASSWORD"]);
		// Named vars for primary environment
		Assert.Equal("https://prod.example.com", result["APP_PRODUCTION_URL"]);
		Assert.Equal("admin", result["APP_PRODUCTION_USERNAME"]);
		Assert.Equal("secret", result["APP_PRODUCTION_PASSWORD"]);
		// Named vars for secondary environment (name sanitized: "Local Dev" -> "LOCAL_DEV")
		Assert.Equal("http://localhost:3000", result["APP_LOCAL_DEV_URL"]);
		Assert.False(result.ContainsKey("APP_LOCAL_DEV_USERNAME"));
		Assert.False(result.ContainsKey("APP_LOCAL_DEV_PASSWORD"));
	}

	[Fact]
	public void BuildJobEnvironmentVariables_FirstEnvironmentBecomesShortcutWhenNoPrimary()
	{
		var service = CreateCredentialService();
		var project = new Project
		{
			Name = "Test",
			WorkingPath = "/tmp",
			Environments =
			[
				new ProjectEnvironment
				{
					Name = "Staging",
					Url = "https://staging.example.com",
					IsEnabled = true,
					IsPrimary = false
				}
			]
		};

		var result = service.BuildJobEnvironmentVariables(project);

		Assert.NotNull(result);
		Assert.Equal("https://staging.example.com", result["APP_URL"]);
		Assert.Equal("https://staging.example.com", result["APP_STAGING_URL"]);
	}

	[Fact]
	public void JobEnvironmentSnapshot_FromProject_ReturnsEmptyForNull()
	{
		var result = JobEnvironmentSnapshot.FromProject(null);
		Assert.Empty(result);
	}

	[Fact]
	public void JobEnvironmentSnapshot_FromProject_CapturesEnabledEnvironments()
	{
		var project = new Project
		{
			Name = "Test",
			WorkingPath = "/tmp",
			Environments =
			[
				new ProjectEnvironment
				{
					Name = "Production",
					Type = EnvironmentType.Web,
					Stage = EnvironmentStage.Production,
					Url = "https://prod.example.com",
					IsPrimary = true,
					IsEnabled = true
				},
				new ProjectEnvironment
				{
					Name = "Disabled",
					Type = EnvironmentType.Web,
					Stage = EnvironmentStage.Development,
					Url = "https://disabled.example.com",
					IsEnabled = false
				},
				new ProjectEnvironment
				{
					Name = "Local",
					Type = EnvironmentType.Web,
					Stage = EnvironmentStage.Local,
					Url = "http://localhost:5000",
					IsEnabled = true
				}
			]
		};

		var snapshots = JobEnvironmentSnapshot.FromProject(project);

		Assert.Equal(2, snapshots.Count);
		Assert.Equal("Production", snapshots[0].Name);
		Assert.True(snapshots[0].IsPrimary);
		Assert.Equal(EnvironmentType.Web, snapshots[0].Type);
		Assert.Equal(EnvironmentStage.Production, snapshots[0].Stage);
		Assert.Equal("https://prod.example.com", snapshots[0].Url);
		Assert.Equal("Local", snapshots[1].Name);
		Assert.False(snapshots[1].IsPrimary);
	}

	[Fact]
	public void JobEnvironmentSnapshot_FromProject_ExcludesCredentials()
	{
		var project = new Project
		{
			Name = "Test",
			WorkingPath = "/tmp",
			Environments =
			[
				new ProjectEnvironment
				{
					Name = "Production",
					Type = EnvironmentType.Web,
					Url = "https://prod.example.com",
					Username = "admin@example.com",
					Password = "SuperSecret!",
					IsEnabled = true
				}
			]
		};

		var snapshots = JobEnvironmentSnapshot.FromProject(project);
		var json = System.Text.Json.JsonSerializer.Serialize(snapshots);

		Assert.Single(snapshots);
		Assert.DoesNotContain("admin@example.com", json);
		Assert.DoesNotContain("SuperSecret!", json);
	}

	[Fact]
	public void Job_EnvironmentSnapshots_DeserializesFromJson()
	{
		var snapshots = new List<JobEnvironmentSnapshot>
		{
			new() { Name = "Production", Url = "https://prod.example.com", Type = EnvironmentType.Web, Stage = EnvironmentStage.Production, IsPrimary = true },
			new() { Name = "Local", Url = "http://localhost:5000", Type = EnvironmentType.Web, Stage = EnvironmentStage.Local, IsPrimary = false }
		};

		var job = new Job
		{
			GoalPrompt = "Test",
			EnvironmentsJson = System.Text.Json.JsonSerializer.Serialize(snapshots),
			PlaywrightEnabled = true,
			EnvironmentCount = 2
		};

		var result = job.EnvironmentSnapshots;
		Assert.Equal(2, result.Count);
		Assert.Equal("Production", result[0].Name);
		Assert.True(result[0].IsPrimary);
		Assert.Equal("Local", result[1].Name);
		Assert.False(result[1].IsPrimary);
	}

	[Fact]
	public void Job_EnvironmentSnapshots_ReturnsEmptyForNullJson()
	{
		var job = new Job { GoalPrompt = "Test", EnvironmentsJson = null };
		Assert.Empty(job.EnvironmentSnapshots);
	}

	[Fact]
	public void Job_EnvironmentSnapshots_ReturnsEmptyForInvalidJson()
	{
		var job = new Job { GoalPrompt = "Test", EnvironmentsJson = "not valid json" };
		Assert.Empty(job.EnvironmentSnapshots);
	}

	[Fact]
	public void BuildSystemPromptRules_IncludesPlaywrightGuidance_WhenWebEnvironmentsExist()
	{
		var rules = PromptBuilder.BuildSystemPromptRules(new Project
		{
			Name = "Web App",
			WorkingPath = "/tmp/web-app",
			Environments =
			[
				new ProjectEnvironment
				{
					Name = "Production",
					Type = EnvironmentType.Web,
					Stage = EnvironmentStage.Production,
					Url = "https://app.example.com",
					IsEnabled = true
				}
			]
		});

		Assert.NotNull(rules);
		Assert.Contains("DEPLOYED ENVIRONMENTS:", rules);
		Assert.Contains("Playwright MCP", rules);
		Assert.Contains("Do not assume localhost", rules);
	}

	[Fact]
	public void BuildSystemPromptRules_OmitsPlaywright_WhenNoWebEnvironments()
	{
		var rules = PromptBuilder.BuildSystemPromptRules(new Project
		{
			Name = "CLI App",
			WorkingPath = "/tmp/cli-app",
			Environments =
			[
				new ProjectEnvironment
				{
					Name = "Release",
					Type = EnvironmentType.Release,
					Stage = EnvironmentStage.Production,
					Url = "https://github.com/example/releases",
					IsEnabled = true
				}
			]
		});

		Assert.NotNull(rules);
		Assert.DoesNotContain("DEPLOYED ENVIRONMENTS:", rules);
		Assert.DoesNotContain("Playwright MCP", rules);
	}

	[Fact]
	public async Task GenerateMcpConfigJsonAsync_OmitsPlaywright_WhenNoWebEnvironments()
	{
		var testSkill = new Skill
		{
			Id = Guid.NewGuid(),
			Name = "test-skill",
			Description = "CLI guidance.",
			Content = "content",
			IsEnabled = true
		};
		var service = new McpConfigService(new FakeSkillService(
			testSkill));

		var json = await service.GenerateMcpConfigJsonAsync(new Project
		{
			Name = "CLI App",
			WorkingPath = "/tmp/cli-app",
			TeamAssignments =
			[
				new ProjectTeamRole
				{
					TeamRoleId = Guid.NewGuid(),
					IsEnabled = true,
					TeamRole = new TeamRole
					{
						Id = Guid.NewGuid(),
						Name = "CLI Reviewer",
						IsEnabled = true,
						SkillLinks =
						[
							new TeamRoleSkill
							{
								SkillId = testSkill.Id,
								Skill = testSkill
							}
						]
					}
				}
			],
			Environments =
			[
				new ProjectEnvironment
				{
					Name = "Release",
					Type = EnvironmentType.Release,
					Url = "https://github.com/example/releases",
					IsEnabled = true
				}
			]
		});

		Assert.NotNull(json);
		Assert.Contains("\"test-skill\"", json);
		Assert.DoesNotContain("\"playwright\"", json);
	}

	[Fact]
	public async Task GenerateMcpConfigJsonAsync_IncludesOnlyProjectAssignedEnabledSkills()
	{
		var assignedSkill = new Skill
		{
			Id = Guid.NewGuid(),
			Name = "bootstrap-ui",
			Description = "Bootstrap UI guidance.",
			Content = "Bootstrap content",
			IsEnabled = true
		};
		var unassignedSkill = new Skill
		{
			Id = Guid.NewGuid(),
			Name = "security-review",
			Description = "Security review guidance.",
			Content = "Security content",
			IsEnabled = true
		};
		var disabledSkill = new Skill
		{
			Id = Guid.NewGuid(),
			Name = "disabled-skill",
			Description = "Disabled skill",
			Content = "Disabled content",
			IsEnabled = false
		};
		var service = new McpConfigService(new FakeSkillService(assignedSkill, unassignedSkill, disabledSkill));

		var json = await service.GenerateMcpConfigJsonAsync(new Project
		{
			Name = "Scoped Skills",
			WorkingPath = "/tmp/scoped-skills",
			TeamAssignments =
			[
				new ProjectTeamRole
				{
					TeamRoleId = Guid.NewGuid(),
					IsEnabled = true,
					TeamRole = new TeamRole
					{
						Id = Guid.NewGuid(),
						Name = "Frontend",
						IsEnabled = true,
						SkillLinks =
						[
							new TeamRoleSkill
							{
								SkillId = assignedSkill.Id,
								Skill = assignedSkill
							},
							new TeamRoleSkill
							{
								SkillId = disabledSkill.Id,
								Skill = disabledSkill
							}
						]
					}
				}
			],
			Environments = []
		});

		Assert.NotNull(json);
		Assert.Contains("\"bootstrap-ui\"", json);
		Assert.DoesNotContain("\"security-review\"", json);
		Assert.DoesNotContain("\"disabled-skill\"", json);
	}

	private ProjectEnvironmentCredentialService CreateCredentialService()
	{
		var dataProtectionProvider = DataProtectionProvider.Create(new DirectoryInfo(_dataProtectionDirectory));
		return new ProjectEnvironmentCredentialService(dataProtectionProvider);
	}

	private static Task WriteProjectMcpConfigAsync(string workingDirectory, string json)
	{
		return File.WriteAllTextAsync(Path.Combine(workingDirectory, ".mcp.json"), json.Trim());
	}

	private VibeSwarmDbContext CreateDbContext() => new(_dbOptions);

	private ProjectService CreateProjectService(VibeSwarmDbContext dbContext)
	{
		var dataProtectionProvider = DataProtectionProvider.Create(new DirectoryInfo(_dataProtectionDirectory));
		var credentialService = new ProjectEnvironmentCredentialService(dataProtectionProvider);
		return new ProjectService(dbContext, credentialService, new NoOpVersionControlService());
	}

	public void Dispose()
	{
		_connection.Dispose();
		if (Directory.Exists(_dataProtectionDirectory))
		{
			Directory.Delete(_dataProtectionDirectory, recursive: true);
		}
	}

	private sealed class FakeSkillService(params Skill[] skills) : ISkillService
	{
		private readonly IReadOnlyList<Skill> _skills = skills;

		public Task<IEnumerable<Skill>> GetAllAsync(CancellationToken cancellationToken = default)
			=> Task.FromResult<IEnumerable<Skill>>(_skills);

		public Task<IEnumerable<Skill>> GetEnabledAsync(CancellationToken cancellationToken = default)
			=> Task.FromResult<IEnumerable<Skill>>(_skills.Where(skill => skill.IsEnabled));

		public Task<Skill?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
			=> Task.FromResult(_skills.FirstOrDefault(skill => skill.Id == id));

		public Task<Skill?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
			=> Task.FromResult(_skills.FirstOrDefault(skill => string.Equals(skill.Name, name, StringComparison.OrdinalIgnoreCase)));

		public Task<Skill> CreateAsync(Skill skill, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Skill> UpdateAsync(Skill skill, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> NameExistsAsync(string name, Guid? excludeId = null, CancellationToken cancellationToken = default)
			=> Task.FromResult(_skills.Any(skill => string.Equals(skill.Name, name, StringComparison.OrdinalIgnoreCase) && skill.Id != excludeId));
		public Task<SkillImportPreview> PreviewImportAsync(SkillImportRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<SkillImportResult> ImportAsync(SkillImportRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<string?> ExpandSkillAsync(string description, Guid providerId, string? modelId = null, CancellationToken cancellationToken = default)
			=> Task.FromResult<string?>(null);
	}

	private sealed class NoOpVersionControlService : IVersionControlService
	{
		public Task<bool> IsGitAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
		public Task<bool> IsGitRepositoryAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult(false);
		public Task<string?> GetCurrentCommitHashAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
		public Task<string?> GetCurrentBranchAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
		public Task<string?> GetRemoteUrlAsync(string workingDirectory, string remoteName = "origin", CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
		public Task<bool> HasUncommittedChangesAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult(false);
		public Task<IReadOnlyList<string>> GetChangedFilesAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
		public Task<string?> GetWorkingDirectoryDiffAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
		public Task<string?> GetCommitRangeDiffAsync(string workingDirectory, string fromCommit, string? toCommit = null, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
		public Task<GitDiffSummary?> GetDiffSummaryAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default) => Task.FromResult<GitDiffSummary?>(null);
		public Task<GitOperationResult> CommitAllChangesAsync(string workingDirectory, string commitMessage, CancellationToken cancellationToken = default, GitCommitOptions? commitOptions = null) => throw new NotSupportedException();
		public Task<GitOperationResult> PushAsync(string workingDirectory, string remoteName = "origin", string? branchName = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> CommitAndPushAsync(string workingDirectory, string commitMessage, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> CreatePullRequestAsync(string workingDirectory, string sourceBranch, string targetBranch, string title, string? body = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> PreviewMergeBranchAsync(string workingDirectory, string sourceBranch, string targetBranch, string remoteName = "origin", CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> MergeBranchAsync(string workingDirectory, string sourceBranch, string targetBranch, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken cancellationToken = default, bool pushAfterMerge = true, IReadOnlyList<MergeConflictResolution>? conflictResolutions = null) => throw new NotSupportedException();
		public Task<IReadOnlyList<GitBranchInfo>> GetBranchesAsync(string workingDirectory, bool includeRemote = true, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GitBranchInfo>>([]);
		public Task<GitOperationResult> FetchAsync(string workingDirectory, string remoteName = "origin", bool prune = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> HardCheckoutBranchAsync(string workingDirectory, string branchName, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> SyncWithOriginAsync(string workingDirectory, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> CloneRepositoryAsync(string repositoryUrl, string targetDirectory, string? branch = null, Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public string GetGitHubCloneUrl(string ownerAndRepo, bool useSsh = true) => throw new NotSupportedException();
		public string? ExtractGitHubRepository(string? remoteUrl) => null;
		public Task<GitOperationResult> CreateBranchAsync(string workingDirectory, string branchName, bool switchToBranch = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitWorkingTreeStatus> GetWorkingTreeStatusAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult(new GitWorkingTreeStatus());
		public Task<GitOperationResult> PreserveChangesAsync(string workingDirectory, string message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> DiscardAllChangesAsync(string workingDirectory, bool includeUntracked = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IReadOnlyList<string>> GetCommitLogAsync(string workingDirectory, string fromCommit, string? toCommit = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> InitializeRepositoryAsync(string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> IsGitHubCliAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
		public Task<bool> IsGitHubCliAuthenticatedAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
		public Task<GitOperationResult> CreateGitHubRepositoryAsync(string workingDirectory, string repositoryName, string? description = null, bool isPrivate = false, Action<string>? progressCallback = null, CancellationToken cancellationToken = default, string? gitignoreTemplate = null, string? licenseTemplate = null, bool initializeReadme = false) => throw new NotSupportedException();
		public Task<GitOperationResult> AddRemoteAsync(string workingDirectory, string remoteName, string remoteUrl, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IReadOnlyDictionary<string, string>> GetRemotesAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
		public Task<GitOperationResult> CloneWithGitHubCliAsync(string ownerRepo, string targetDirectory, Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> PruneRemoteBranchesAsync(string workingDirectory, string remoteName = "origin", CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}
}
