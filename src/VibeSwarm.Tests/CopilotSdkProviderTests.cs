using GitHub.Copilot.SDK;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

public sealed class CopilotSdkProviderTests
{
	[Fact]
	public void ApplySessionDefaults_SetsApproveAllPermissionHandler()
	{
		var config = new SessionConfig();

		CopilotSdkProvider.ApplySessionDefaults(config);

		Assert.NotNull(config.OnPermissionRequest);
	}

	[Fact]
	public void ApplyResumeSessionDefaults_SetsApproveAllPermissionHandler()
	{
		var config = new ResumeSessionConfig();

		CopilotSdkProvider.ApplyResumeSessionDefaults(config);

		Assert.NotNull(config.OnPermissionRequest);
	}

	[Fact]
	public void GetFallbackAvailableModels_IncludesCurrentCopilotModels()
	{
		var models = CopilotSdkProvider.GetFallbackAvailableModels();

		Assert.Contains("gpt-5.4-mini", models);
		Assert.Contains("gpt-4.1", models);
		Assert.Contains("claude-opus-4.6-fast", models);
	}

	[Fact]
	public void GetFallbackAvailableModels_OmitsRemovedLegacyCodexModels()
	{
		var models = CopilotSdkProvider.GetFallbackAvailableModels();

		Assert.DoesNotContain("gpt-5.1-codex", models);
		Assert.DoesNotContain("gpt-5.1-codex-mini", models);
		Assert.DoesNotContain("gpt-5.1-codex-max", models);
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

		Assert.Equal("/repo", options.Cwd);
		Assert.NotNull(options.Environment);
		Assert.Equal("https://app.example.com", options.Environment!["APP_URL"]);
		Assert.Equal("admin@example.com", options.Environment["APP_USERNAME"]);
		Assert.Equal("secret", options.Environment["APP_PASSWORD"]);
		Assert.Equal("/tmp/bash-env.sh", options.Environment["BASH_ENV"]);
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
			options.CliArgs!.ToList());
	}

	[Fact]
	public void BuildClientOptions_WithoutExecutionContext_OmitsStartupArgsAndEnvironment()
	{
		var provider = new CopilotSdkProvider(CreateConfig());

		var options = provider.BuildClientOptions("/repo");

		Assert.Equal("/repo", options.Cwd);
		Assert.Null(options.Environment);
		Assert.Null(options.CliArgs);
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
