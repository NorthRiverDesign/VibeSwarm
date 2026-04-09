using GitHub.Copilot.SDK;
using VibeSwarm.Shared.Providers;

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
}
