using Microsoft.Copilot;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

public sealed class CopilotSdkProviderTests
{
	[Fact]
	public void ApplySessionDefaults_SetsApproveAllPermissionHandler()
	{
		var config = new CreateSessionOptions();

		CopilotSdkProvider.ApplySessionDefaults(config);

		Assert.NotNull(config.Permissions?.OnRequest);
	}

	[Fact]
	public void ApplyRunSettings_SendsTheJobRulesAndBlocksPlanningTools()
	{
		var provider = new CopilotSdkProvider(CreateConfig());
		provider.ApplyOptions(new ExecutionOptions
		{
			AppendSystemPrompt = "JOB RULES",
			ReasoningEffort = "high",
			DisallowedTools = ProviderPlanningHelper.PlanningDisallowedTools
		});

		foreach (SessionOptionsBase config in new SessionOptionsBase[] { new CreateSessionOptions(), new ResumeSessionOptions() })
		{
			provider.ApplyRunSettings(config, "/repo");

			Assert.Null(config.Model);
			Assert.Equal("/repo", config.WorkingDirectory);
			Assert.True(config.Events!.Streaming);
			Assert.Equal("high", config.Inference!.ReasoningEffort);
			Assert.Equal(SystemMessageMode.Append, config.SystemMessage!.Mode);
			Assert.Equal("JOB RULES", config.SystemMessage.Content);
			Assert.Contains("bash", config.Tools!.ExcludedTools!);
			Assert.Contains("edit", config.Tools.ExcludedTools!);
			Assert.DoesNotContain("Bash", config.Tools.ExcludedTools!);
		}
	}

	[Fact]
	public void ApplyRunSettings_TrustsTheCheckoutForANewSessionOnly()
	{
		var provider = new CopilotSdkProvider(CreateConfig());
		provider.ApplyOptions(new ExecutionOptions());
		var config = new CreateSessionOptions();

		provider.ApplyRunSettings(config, "/repo");

		Assert.Equal(WorkingDirectoryTrust.Session, config.Filesystem!.TrustWorkingDirectory);
	}

	[Fact]
	public void ApplyResumeSessionDefaults_SetsApproveAllPermissionHandler()
	{
		var config = new ResumeSessionOptions();

		CopilotSdkProvider.ApplyResumeSessionDefaults(config);

		Assert.NotNull(config.Permissions?.OnRequest);
	}

	[Fact]
	public void GetFallbackAvailableModels_IncludesCurrentCopilotModels()
	{
		var models = CopilotSdkProvider.GetFallbackAvailableModels();

		Assert.Contains("claude-sonnet-5.5", models);
		Assert.Contains("gemini-4-hydrogen", models);
		Assert.Contains("gpt-5.4-mini", models);
		Assert.Contains("gpt-4.1", models);
		Assert.Equal(models.Count, models.Distinct(StringComparer.Ordinal).Count());
	}

	[Fact]
	public void GetFallbackAvailableModels_OmitsRemovedLegacyCodexModels()
	{
		var models = CopilotSdkProvider.GetFallbackAvailableModels();

		Assert.DoesNotContain("gpt-5.1-codex", models);
		Assert.DoesNotContain("gpt-5.1-codex-mini", models);
		Assert.DoesNotContain("gpt-5.1-codex-max", models);
		// Deprecated in Copilot CLI 1.0.66.
		Assert.DoesNotContain("claude-opus-4.6-fast", models);
	}

	[Fact]
	public void BuildClientOptions_WithExecutionContext_IncludesStartupArgsAndEnvironment()
	{
		var provider = new CopilotSdkProvider(CreateConfig());
		provider.ApplyOptions(new ExecutionOptions
		{
			McpConfigPath = "/tmp/mcp.json",
			BashEnvPath = "/tmp/bash-env.sh",
			AdditionalDirectories = ["/tmp/worktree", " /tmp/review ", "/tmp/worktree"],
			EnvironmentVariables = new Dictionary<string, string>(StringComparer.Ordinal)
			{
				["APP_URL"] = "https://app.example.com",
				["APP_USERNAME"] = "admin@example.com",
				["APP_PASSWORD"] = "secret"
			}
		});

		var options = provider.BuildClientOptions("/repo");
		var runtime = Assert.IsType<SpawnedStdioRuntimeConnection>(options.Runtime);

		Assert.Equal(CopilotClientMode.CodingAgent, options.Mode);
		Assert.Equal("/repo", runtime.WorkingDirectory);
		Assert.NotNull(runtime.Environment);
		Assert.Equal("https://app.example.com", runtime.Environment!["APP_URL"]);
		Assert.Equal("admin@example.com", runtime.Environment["APP_USERNAME"]);
		Assert.Equal("secret", runtime.Environment["APP_PASSWORD"]);
		Assert.Equal("/tmp/bash-env.sh", runtime.Environment["BASH_ENV"]);
		Assert.Equal(
			[
				"--bash-env",
				"on",
				"--additional-mcp-config",
				"@/tmp/mcp.json",
				"--add-dir",
				"/tmp/worktree",
				"--add-dir",
				"/tmp/review"
			],
			runtime.Args!.ToList());
	}

	[Fact]
	public void BuildClientOptions_WithoutExecutionContext_OmitsStartupArgsAndEnvironment()
	{
		var provider = new CopilotSdkProvider(CreateConfig());

		var options = provider.BuildClientOptions("/repo");
		var runtime = Assert.IsType<SpawnedStdioRuntimeConnection>(options.Runtime);

		Assert.Equal("/repo", runtime.WorkingDirectory);
		Assert.Null(runtime.Environment);
		Assert.Null(runtime.Args);
	}

	[Fact]
	public void BuildClientOptions_WithGitHubToken_SignsInWithTheTokenNotTheLoggedInUser()
	{
		var config = CreateConfig();
		config.ApiKey = "github_pat_example";
		var provider = new CopilotSdkProvider(config);

		var options = provider.BuildClientOptions("/repo");

		Assert.Equal("github_pat_example", options.GitHub!.Token);
		Assert.False(options.GitHub.UseLoggedInUser);
	}

	[Fact]
	public void ApplyRunSettings_WithByokEndpoint_RoutesEveryModelToThatEndpoint()
	{
		var config = CreateConfig();
		config.ApiEndpoint = "https://api.openai.com/v1";
		config.ApiKey = "sk-example";
		var provider = new CopilotSdkProvider(config);
		provider.ApplyOptions(new ExecutionOptions { ReasoningEffort = "high" });
		var session = new CreateSessionOptions();

		provider.ApplyRunSettings(session, "/repo");

#pragma warning disable MCPSDK001 // Named provider connections are marked experimental in the SDK
		var connection = Assert.Single(session.Inference!.Providers!);
		Assert.Equal("openai", connection.Type);
		Assert.Equal("https://api.openai.com/v1", connection.BaseUrl);
		Assert.Equal("sk-example", connection.ApiKey);
		Assert.Equal(connection.Name, session.Inference.DefaultProvider);
#pragma warning restore MCPSDK001
		Assert.Equal("high", session.Inference.ReasoningEffort);
		Assert.False(session.GitHub!.Capi!.Enabled);
	}

	[Fact]
	public void DefaultPromptTimeout_MatchesSharedJobExecutionDefault()
	{
		Assert.Equal(JobCompletionCriteria.DefaultStallTimeoutValue, CopilotSdkProvider.DefaultPromptTimeout);
	}

	private static Provider CreateConfig()
	{
		return new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Copilot SDK",
			Type = ProviderType.Copilot,
			ConnectionMode = ProviderConnectionMode.SDK
		};
	}
}
