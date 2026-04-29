using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Tests;

/// <summary>
/// Regression tests for CLI argument building in each provider.
/// These tests ensure that only supported flags are emitted per provider,
/// and that flags from one provider never leak into another.
/// </summary>
public sealed class ProviderCliArgsTests
{
    private static Provider CreateConfig(ProviderType type) => new()
    {
        Id = Guid.NewGuid(),
        Name = $"Test {type}",
        Type = type,
        ConnectionMode = ProviderConnectionMode.CLI,
        ExecutablePath = type switch
        {
            ProviderType.Claude => "claude",
            ProviderType.Copilot => "copilot",
            ProviderType.OpenCode => "opencode",
            _ => "test"
        }
    };

    // ─── Claude Provider ───────────────────────────────────────────────

    [Fact]
    public void Claude_MinimalOptions_ContainsRequiredFlags()
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));
        provider.ApplyOptions(new ExecutionOptions());

        var args = provider.BuildCliArgs("do something", null);

        Assert.Contains("-p", args);
        Assert.Contains("do something", args);
        Assert.Contains("--output-format", args);
        Assert.Contains("stream-json", args);
        Assert.Contains("--verbose", args);
        Assert.Contains("--permission-mode", args);
        Assert.Contains("bypassPermissions", args);
    }

    [Fact]
    public void Claude_WithModel_AddsModelFlag()
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));
        provider.ApplyOptions(new ExecutionOptions { Model = "opus" });

        var args = provider.BuildCliArgs("test", null);

        var idx = args.IndexOf("--model");
        Assert.True(idx >= 0);
        Assert.Equal("opus", args[idx + 1]);
    }

    [Fact]
    public void Claude_WithBareMode_AndSupportedVersion_AddsBareFlag()
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));
        provider.CachedCliVersion = new Version(2, 1, 81);
        provider.ApplyOptions(new ExecutionOptions { UseBareMode = true });

        var args = provider.BuildCliArgs("test", null);

        Assert.Contains("--bare", args);
    }

    [Fact]
    public void Claude_WithBareMode_AndOldVersion_OmitsBareFlag()
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));
        provider.CachedCliVersion = new Version(2, 1, 80);
        provider.ApplyOptions(new ExecutionOptions { UseBareMode = true });

        var args = provider.BuildCliArgs("test", null);

        Assert.DoesNotContain("--bare", args);
    }

    [Fact]
    public void Claude_WithSessionId_AddsResumeFlag()
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));
        provider.ApplyOptions(new ExecutionOptions());

        var args = provider.BuildCliArgs("test", "session-123");

        var idx = args.IndexOf("--resume");
        Assert.True(idx >= 0);
        Assert.Equal("session-123", args[idx + 1]);
        Assert.DoesNotContain("--continue", args);
    }

    [Fact]
    public void Claude_WithContinueSession_AddsContinueFlag()
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));
        provider.ApplyOptions(new ExecutionOptions { ContinueLastSession = true });

        var args = provider.BuildCliArgs("test", null);

        Assert.Contains("--continue", args);
        Assert.DoesNotContain("--resume", args);
    }

    [Fact]
    public void Claude_WithAgent_AndSupportedVersion_AddsAgentFlag()
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));
        provider.CachedCliVersion = new Version(2, 1, 64);
        provider.ApplyOptions(new ExecutionOptions { Agent = " reviewer " });

        var args = provider.BuildCliArgs("test", null);

        var idx = args.IndexOf("--agent");
        Assert.True(idx >= 0);
        Assert.Equal("reviewer", args[idx + 1]);
    }

    [Fact]
    public void Claude_WithAgent_AndOldVersion_OmitsAgentFlag()
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));
        provider.CachedCliVersion = new Version(2, 1, 63);
        provider.ApplyOptions(new ExecutionOptions { Agent = "reviewer" });

        var args = provider.BuildCliArgs("test", null);

        Assert.DoesNotContain("--agent", args);
    }

    [Fact]
    public void Claude_WithMaxTurns_AddsMaxTurnsFlag()
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));
        provider.ApplyOptions(new ExecutionOptions { MaxTurns = 10 });

        var args = provider.BuildCliArgs("test", null);

        var idx = args.IndexOf("--max-turns");
        Assert.True(idx >= 0);
        Assert.Equal("10", args[idx + 1]);
    }

    [Fact]
    public void Claude_WithSystemPrompt_AddsSystemPromptFlag()
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));
        provider.ApplyOptions(new ExecutionOptions { SystemPrompt = "be helpful" });

        var args = provider.BuildCliArgs("test", null);

        var idx = args.IndexOf("--system-prompt");
        Assert.True(idx >= 0);
        Assert.Equal("be helpful", args[idx + 1]);
    }

    [Fact]
    public void Claude_WithAppendSystemPrompt_AddsAppendFlag()
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));
        provider.ApplyOptions(new ExecutionOptions { AppendSystemPrompt = "extra rules" });

        var args = provider.BuildCliArgs("test", null);

        var idx = args.IndexOf("--append-system-prompt");
        Assert.True(idx >= 0);
        Assert.Equal("extra rules", args[idx + 1]);
    }

    [Fact]
    public void Claude_WithAdditionalDirectories_AddsDistinctTrimmedAddDirFlags()
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));
        provider.ApplyOptions(new ExecutionOptions
        {
            AdditionalDirectories = [" /repo/shared ", "/repo/shared", "", "   ", "/repo/docs"]
        });

        var args = provider.BuildCliArgs("test", null);

        var indices = args.Select((a, i) => (a, i)).Where(x => x.a == "--add-dir").Select(x => x.i).ToList();
        Assert.Equal(2, indices.Count);
        Assert.Equal("/repo/shared", args[indices[0] + 1]);
        Assert.Equal("/repo/docs", args[indices[1] + 1]);
    }

    [Fact]
    public void Claude_WithAllowedTools_AddsMultipleToolsFlags()
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));
        provider.ApplyOptions(new ExecutionOptions { AllowedTools = ["Read", "Write", "Bash"] });

        var args = provider.BuildCliArgs("test", null);

        var toolsIndices = args.Select((a, i) => (a, i)).Where(x => x.a == "--tools").Select(x => x.i).ToList();
        Assert.Equal(3, toolsIndices.Count);
        Assert.Equal("Read", args[toolsIndices[0] + 1]);
        Assert.Equal("Write", args[toolsIndices[1] + 1]);
        Assert.Equal("Bash", args[toolsIndices[2] + 1]);
    }

    [Fact]
    public void Claude_WithDisallowedTools_AddsDisallowedToolsFlags()
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));
        provider.CachedCliVersion = new Version(2, 1, 0);
        provider.ApplyOptions(new ExecutionOptions { DisallowedTools = ["Bash"] });

        var args = provider.BuildCliArgs("test", null);

        var idx = args.IndexOf("--disallowed-tools");
        Assert.True(idx >= 0);
        Assert.Equal("Bash", args[idx + 1]);
    }

    [Fact]
    public void Claude_WithWorktree_AddsWorktreeFlag()
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));
        provider.CachedCliVersion = new Version(2, 1, 49);
        provider.ApplyOptions(new ExecutionOptions { UseWorktree = true });

        var args = provider.BuildCliArgs("test", null);

        Assert.Contains("--worktree", args);
    }

    [Fact]
    public void Claude_WithMcpConfig_AddsMcpConfigFlag()
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));
        provider.ApplyOptions(new ExecutionOptions { McpConfigPath = "/tmp/mcp.json" });

        var args = provider.BuildCliArgs("test", null);

        var idx = args.IndexOf("--mcp-config");
        Assert.True(idx >= 0);
        Assert.Equal("/tmp/mcp.json", args[idx + 1]);
    }

    [Fact]
    public void Claude_WithReasoningEffort_AddsEffortFlag()
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));
        provider.CachedCliVersion = new Version(2, 1, 63);
        provider.ApplyOptions(new ExecutionOptions { ReasoningEffort = "high" });

        var args = provider.BuildCliArgs("test", null);

        var idx = args.IndexOf("--effort");
        Assert.True(idx >= 0);
        Assert.Equal("high", args[idx + 1]);
    }

    [Fact]
    public void Claude_WithMaxReasoningEffort_AddsEffortFlag()
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));
        provider.CachedCliVersion = new Version(2, 1, 87);
        provider.ApplyOptions(new ExecutionOptions { ReasoningEffort = "max" });

        var args = provider.BuildCliArgs("test", null);

        var idx = args.IndexOf("--effort");
        Assert.True(idx >= 0);
        Assert.Equal("max", args[idx + 1]);
    }

    [Fact]
    public void Claude_WithMaxBudget_AddsMaxBudgetFlag()
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));
        provider.CachedCliVersion = new Version(2, 0, 28);
        provider.ApplyOptions(new ExecutionOptions { MaxBudgetUsd = 5.00m });

        var args = provider.BuildCliArgs("test", null);

        var idx = args.IndexOf("--max-budget-usd");
        Assert.True(idx >= 0);
        Assert.Equal("5.00", args[idx + 1]);
    }

    [Fact]
    public void Claude_WithFromPr_AddsFromPrFlag()
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));
        provider.CachedCliVersion = new Version(2, 1, 27);
        provider.ApplyOptions(new ExecutionOptions { FromPullRequest = "42" });

        var args = provider.BuildCliArgs("test", null);

        var idx = args.IndexOf("--from-pr");
        Assert.True(idx >= 0);
        Assert.Equal("42", args[idx + 1]);
    }

    [Fact]
    public void Claude_WithDisallowedTools_AndOldVersion_OmitsDisallowedToolsFlag()
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));
        provider.CachedCliVersion = new Version(2, 0, 99);
        provider.ApplyOptions(new ExecutionOptions { DisallowedTools = ["Bash"] });

        var args = provider.BuildCliArgs("test", null);

        Assert.DoesNotContain("--disallowed-tools", args);
    }

    [Fact]
    public void Claude_WithWorktree_AndOldVersion_OmitsWorktreeFlag()
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));
        provider.CachedCliVersion = new Version(2, 1, 48);
        provider.ApplyOptions(new ExecutionOptions { UseWorktree = true });

        var args = provider.BuildCliArgs("test", null);

        Assert.DoesNotContain("--worktree", args);
    }

    [Fact]
    public void Claude_WithReasoningEffort_AndOldVersion_OmitsEffortFlag()
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));
        provider.CachedCliVersion = new Version(2, 1, 62);
        provider.ApplyOptions(new ExecutionOptions { ReasoningEffort = "high" });

        var args = provider.BuildCliArgs("test", null);

        Assert.DoesNotContain("--effort", args);
    }

    [Fact]
    public void Claude_WithMaxBudget_AndOldVersion_OmitsMaxBudgetFlag()
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));
        provider.CachedCliVersion = new Version(2, 0, 27);
        provider.ApplyOptions(new ExecutionOptions { MaxBudgetUsd = 5.00m });

        var args = provider.BuildCliArgs("test", null);

        Assert.DoesNotContain("--max-budget-usd", args);
    }

    [Fact]
    public void Claude_WithFromPr_AndOldVersion_OmitsFromPrFlag()
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));
        provider.CachedCliVersion = new Version(2, 1, 26);
        provider.ApplyOptions(new ExecutionOptions { FromPullRequest = "42" });

        var args = provider.BuildCliArgs("test", null);

        Assert.DoesNotContain("--from-pr", args);
    }

    [Fact]
    public void Claude_WithInitMode_AndSupportedVersion_AddsInitFlag()
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));
        provider.CachedCliVersion = new Version(2, 1, 10);
        provider.ApplyOptions(new ExecutionOptions { InitMode = "ide" });

        var args = provider.BuildCliArgs("test", null);

        Assert.Contains("--ide", args);
    }

    [Fact]
    public void Claude_WithInitMode_AndOldVersion_OmitsInitFlag()
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));
        provider.CachedCliVersion = new Version(2, 1, 9);
        provider.ApplyOptions(new ExecutionOptions { InitMode = "ide" });

        var args = provider.BuildCliArgs("test", null);

        Assert.DoesNotContain("--ide", args);
    }

    [Fact]
    public void Claude_DoesNotContain_CopilotSpecificFlags()
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));
        provider.ApplyOptions(new ExecutionOptions
        {
            Model = "opus",
            MaxTurns = 10,
            McpConfigPath = "/tmp/mcp.json"
        });

        var args = provider.BuildCliArgs("test", null);

        Assert.DoesNotContain("--yolo", args);
        Assert.DoesNotContain("--silent", args);
        Assert.DoesNotContain("--autopilot", args);
        Assert.DoesNotContain("--bash-env", args);
        Assert.DoesNotContain("--no-mouse", args);
        Assert.DoesNotContain("--alt-screen", args);
        Assert.DoesNotContain("--max-autopilot-continues", args);
        Assert.DoesNotContain("--reasoning-effort", args);
        Assert.DoesNotContain("--available-tools", args);
        Assert.DoesNotContain("--excluded-tools", args);
        Assert.DoesNotContain("--deny-tool", args);
        Assert.DoesNotContain("--additional-mcp-config", args);
    }

    [Fact]
    public void Claude_WithCustomPermissionMode_UsesProvidedMode()
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));
        provider.ApplyOptions(new ExecutionOptions { PermissionMode = "dontAsk" });

        var args = provider.BuildCliArgs("test", null);

        var idx = args.IndexOf("--permission-mode");
        Assert.True(idx >= 0);
        Assert.Equal("dontAsk", args[idx + 1]);
    }

    [Fact]
    public void Claude_AlwaysSetsUnattendedHardeningEnvVars()
    {
        // DISABLE_UPDATES and CLAUDE_CODE_SUBPROCESS_ENV_SCRUB must be set on every
        // Claude invocation regardless of options or stall configuration.
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));

        var env = provider.BaseEnvironmentVariables;

        Assert.NotNull(env);
        Assert.Equal("1", env!["DISABLE_UPDATES"]);
        Assert.Equal("1", env["CLAUDE_CODE_SUBPROCESS_ENV_SCRUB"]);
    }

    [Fact]
    public void Claude_WithStallTimeout_SetsStreamIdleTimeoutEnvVar()
    {
        var config = CreateConfig(ProviderType.Claude);
        config.StallTimeoutSeconds = 600;
        var provider = new ClaudeProvider(config);

        var env = provider.BaseEnvironmentVariables;

        Assert.NotNull(env);
        Assert.Equal("600000", env!["CLAUDE_STREAM_IDLE_TIMEOUT_MS"]);
    }

    [Fact]
    public void Claude_WithoutStallTimeout_OmitsStreamIdleTimeoutEnvVar()
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));

        var env = provider.BaseEnvironmentVariables;

        Assert.NotNull(env);
        Assert.False(env!.ContainsKey("CLAUDE_STREAM_IDLE_TIMEOUT_MS"));
    }

    // ─── Copilot Provider ──────────────────────────────────────────────

	[Fact]
	public void Copilot_MinimalOptions_ContainsRequiredFlags()
	{
		var provider = new CopilotProvider(CreateConfig(ProviderType.Copilot));
		provider.ApplyOptions(new ExecutionOptions());

        var args = provider.BuildCliArgs("do something", null);

        Assert.Contains("-p", args);
        Assert.Contains("--yolo", args);
        Assert.Contains("--silent", args);
		Assert.Contains("--autopilot", args);
		Assert.Contains("--no-mouse", args);
	}

	[Fact]
	public void Copilot_WithJsonOutputSupportedVersion_AddsOutputFormatJson()
	{
		var provider = new CopilotProvider(CreateConfig(ProviderType.Copilot));
		provider.CachedCliVersion = new Version(0, 0, 422);
		provider.ApplyOptions(new ExecutionOptions());

		var args = provider.BuildCliArgs("test", null);

		var idx = args.IndexOf("--output-format");
		Assert.True(idx >= 0);
		Assert.Equal("json", args[idx + 1]);
		Assert.DoesNotContain("--silent", args);
	}

	[Fact]
	public void Copilot_WithUnknownVersion_OmitsOutputFormatJson()
	{
		var provider = new CopilotProvider(CreateConfig(ProviderType.Copilot));
		provider.ApplyOptions(new ExecutionOptions());

		var args = provider.BuildCliArgs("test", null);

		Assert.DoesNotContain("--output-format", args);
	}

    [Fact]
    public void Copilot_WithModel_AddsModelFlag()
    {
        var provider = new CopilotProvider(CreateConfig(ProviderType.Copilot));
        provider.ApplyOptions(new ExecutionOptions { Model = "gpt-4" });

        var args = provider.BuildCliArgs("test", null);

        var idx = args.IndexOf("--model");
        Assert.True(idx >= 0);
        Assert.Equal("gpt-4", args[idx + 1]);
    }

	[Fact]
	public void Copilot_WithAdditionalDirectories_AddsDistinctTrimmedAddDirFlags()
	{
		var provider = new CopilotProvider(CreateConfig(ProviderType.Copilot));
		provider.ApplyOptions(new ExecutionOptions
		{
			AdditionalDirectories = [" /repo/shared ", "/repo/shared", "", "   ", "/repo/docs"]
		});

		var args = provider.BuildCliArgs("test", null);

		var indices = args.Select((a, i) => (a, i)).Where(x => x.a == "--add-dir").Select(x => x.i).ToList();
		Assert.Equal(2, indices.Count);
		Assert.Equal("/repo/shared", args[indices[0] + 1]);
		Assert.Equal("/repo/docs", args[indices[1] + 1]);
	}

    [Fact]
    public void Copilot_WithSessionId_AddsResumeFlag()
    {
        var provider = new CopilotProvider(CreateConfig(ProviderType.Copilot));
        provider.ApplyOptions(new ExecutionOptions());

        var args = provider.BuildCliArgs("test", "sess-abc");

        var idx = args.IndexOf("--resume");
        Assert.True(idx >= 0);
        Assert.Equal("sess-abc", args[idx + 1]);
    }

    [Fact]
    public void Copilot_WithMaxTurns_AddsMaxAutopilotContinues()
    {
        var provider = new CopilotProvider(CreateConfig(ProviderType.Copilot));
        provider.ApplyOptions(new ExecutionOptions { MaxTurns = 5 });

        var args = provider.BuildCliArgs("test", null);

        var idx = args.IndexOf("--max-autopilot-continues");
        Assert.True(idx >= 0);
        Assert.Equal("5", args[idx + 1]);
        Assert.DoesNotContain("--max-turns", args);
    }

    [Fact]
    public void Copilot_WithBashEnvPath_AndSupportedVersion_AddsBashEnvOnFlag()
    {
        var provider = new CopilotProvider(CreateConfig(ProviderType.Copilot));
        provider.CachedCliVersion = new Version(0, 0, 418);
        provider.ApplyOptions(new ExecutionOptions { BashEnvPath = "/tmp/bash-env.sh" });

        var args = provider.BuildCliArgs("test", null);

        var idx = args.IndexOf("--bash-env");
        Assert.True(idx >= 0);
        Assert.Equal("on", args[idx + 1]);
    }

    [Fact]
    public void Copilot_WithBashEnvPath_AndOldVersion_OmitsBashEnvFlag()
    {
        // Versions before 0.0.418 only accept "on"/"off", not a file path.
        // VibeSwarm must not pass the file path to avoid crashing the job on older installs.
        var provider = new CopilotProvider(CreateConfig(ProviderType.Copilot));
        provider.CachedCliVersion = new Version(0, 0, 417);
        provider.ApplyOptions(new ExecutionOptions { BashEnvPath = "/tmp/bash-env.sh" });

        var args = provider.BuildCliArgs("test", null);

        Assert.DoesNotContain("--bash-env", args);
    }

    [Fact]
    public void Copilot_WithBashEnvPath_AndUnknownVersion_OmitsBashEnvFlag()
    {
        // When the CLI version is unknown (null), skip --bash-env to be safe on older installs.
        var provider = new CopilotProvider(CreateConfig(ProviderType.Copilot));
        // CachedCliVersion left as null (default)
        provider.ApplyOptions(new ExecutionOptions { BashEnvPath = "/tmp/bash-env.sh" });

        var args = provider.BuildCliArgs("test", null);

        Assert.DoesNotContain("--bash-env", args);
    }

    [Fact]
    public void Copilot_WithoutBashEnvPath_OmitsBashEnvFlag()
    {
        var provider = new CopilotProvider(CreateConfig(ProviderType.Copilot));
        provider.ApplyOptions(new ExecutionOptions());

        var args = provider.BuildCliArgs("test", null);

        Assert.DoesNotContain("--bash-env", args);
    }

    [Fact]
    public void Copilot_WithAltScreen_AddsAltScreenFlag()
    {
        var provider = new CopilotProvider(CreateConfig(ProviderType.Copilot));
        // --alt-screen was removed in v1.0.8; v1.0.7 is the last version that accepts it.
        provider.CachedCliVersion = new Version(1, 0, 7);
        provider.ApplyOptions(new ExecutionOptions { UseAltScreen = true });

        var args = provider.BuildCliArgs("test", null);

        var idx = args.IndexOf("--alt-screen");
        Assert.True(idx >= 0);
        Assert.Equal("on", args[idx + 1]);
    }

    [Fact]
    public void Copilot_WithAltScreen_AndRemovedVersion_OmitsAltScreenFlag()
    {
        var provider = new CopilotProvider(CreateConfig(ProviderType.Copilot));
        // --alt-screen was removed in v1.0.8 ("alt screen always enabled").
        provider.CachedCliVersion = new Version(1, 0, 8);
        provider.ApplyOptions(new ExecutionOptions { UseAltScreen = true });

        var args = provider.BuildCliArgs("test", null);

        Assert.DoesNotContain("--alt-screen", args);
    }

    [Fact]
    public void Copilot_WithAltScreen_AndUnknownVersion_OmitsAltScreenFlag()
    {
        var provider = new CopilotProvider(CreateConfig(ProviderType.Copilot));
        provider.ApplyOptions(new ExecutionOptions { UseAltScreen = true });

        var args = provider.BuildCliArgs("test", null);

        Assert.DoesNotContain("--alt-screen", args);
    }

    [Fact]
    public void Copilot_WithStallTimeout_AndSupportedVersion_AddsSessionIdleTimeoutFlag()
    {
        var config = CreateConfig(ProviderType.Copilot);
        config.StallTimeoutSeconds = 600;
        var provider = new CopilotProvider(config);
        provider.CachedCliVersion = new Version(1, 0, 35);
        provider.ApplyOptions(new ExecutionOptions());

        var args = provider.BuildCliArgs("test", null);

        var idx = args.IndexOf("--session-idle-timeout");
        Assert.True(idx >= 0, "--session-idle-timeout should be emitted on v1.0.35+");
        Assert.Equal("600", args[idx + 1]);
    }

    [Fact]
    public void Copilot_WithStallTimeout_AndOldVersion_OmitsSessionIdleTimeoutFlag()
    {
        var config = CreateConfig(ProviderType.Copilot);
        config.StallTimeoutSeconds = 600;
        var provider = new CopilotProvider(config);
        provider.CachedCliVersion = new Version(1, 0, 34);
        provider.ApplyOptions(new ExecutionOptions());

        var args = provider.BuildCliArgs("test", null);

        Assert.DoesNotContain("--session-idle-timeout", args);
    }

    [Fact]
    public void Copilot_WithoutStallTimeout_OmitsSessionIdleTimeoutFlag()
    {
        var provider = new CopilotProvider(CreateConfig(ProviderType.Copilot));
        provider.CachedCliVersion = new Version(1, 0, 35);
        provider.ApplyOptions(new ExecutionOptions());

        var args = provider.BuildCliArgs("test", null);

        Assert.DoesNotContain("--session-idle-timeout", args);
    }

    [Fact]
    public void Copilot_WithMcpConfig_AddsAdditionalMcpConfigWithAtPrefix()
    {
        var provider = new CopilotProvider(CreateConfig(ProviderType.Copilot));
        provider.ApplyOptions(new ExecutionOptions { McpConfigPath = "/tmp/mcp.json" });

        var args = provider.BuildCliArgs("test", null);

        var idx = args.IndexOf("--additional-mcp-config");
        Assert.True(idx >= 0);
        Assert.Equal("@/tmp/mcp.json", args[idx + 1]);
    }

    [Fact]
    public void Copilot_WithAllowedTools_AddsAvailableToolsFlags()
    {
        var provider = new CopilotProvider(CreateConfig(ProviderType.Copilot));
        provider.ApplyOptions(new ExecutionOptions { AllowedTools = ["Read", "Write"] });

        var args = provider.BuildCliArgs("test", null);

        var indices = args.Select((a, i) => (a, i)).Where(x => x.a == "--available-tools").Select(x => x.i).ToList();
        Assert.Equal(2, indices.Count);
        Assert.DoesNotContain("--tools", args);
    }

    [Fact]
    public void Copilot_WithExcludedTools_AddsExcludedToolsFlags()
    {
        var provider = new CopilotProvider(CreateConfig(ProviderType.Copilot));
        provider.ApplyOptions(new ExecutionOptions { ExcludedTools = ["Bash"] });

        var args = provider.BuildCliArgs("test", null);

        var idx = args.IndexOf("--excluded-tools");
        Assert.True(idx >= 0);
        Assert.Equal("Bash", args[idx + 1]);
    }

    [Fact]
    public void Copilot_WithDeniedTools_AddsDenyToolFlags()
    {
        var provider = new CopilotProvider(CreateConfig(ProviderType.Copilot));
        provider.ApplyOptions(new ExecutionOptions { DisallowedTools = ["Bash"] });

        var args = provider.BuildCliArgs("test", null);

        var idx = args.IndexOf("--deny-tool");
        Assert.True(idx >= 0);
        Assert.Equal("Bash", args[idx + 1]);
        Assert.DoesNotContain("--disallowed-tools", args);
    }

    [Fact]
    public void Copilot_WithReasoningEffort_AndSupportedVersion_AddsReasoningEffortFlag()
    {
        var provider = new CopilotProvider(CreateConfig(ProviderType.Copilot));
        provider.CachedCliVersion = new Version(1, 0, 4);
        provider.ApplyOptions(new ExecutionOptions { ReasoningEffort = "xhigh" });

        var args = provider.BuildCliArgs("test", null);

        var idx = args.IndexOf("--reasoning-effort");
        Assert.True(idx >= 0, "--reasoning-effort should be present on v1.0.4+");
        Assert.Equal("xhigh", args[idx + 1]);
        Assert.DoesNotContain("--effort", args);
    }

    [Fact]
    public void Copilot_WithReasoningEffort_AndOldVersion_OmitsReasoningEffortFlag()
    {
        var provider = new CopilotProvider(CreateConfig(ProviderType.Copilot));
        provider.CachedCliVersion = new Version(0, 0, 418); // pre-1.0.4
        provider.ApplyOptions(new ExecutionOptions { ReasoningEffort = "medium" });

        var args = provider.BuildCliArgs("test", null);

        Assert.DoesNotContain("--reasoning-effort", args);
        Assert.DoesNotContain("--effort", args);
    }

    [Fact]
    public void Copilot_WithReasoningEffort_AndUnknownVersion_OmitsReasoningEffortFlag()
    {
        var provider = new CopilotProvider(CreateConfig(ProviderType.Copilot));
        // CachedCliVersion left null (no TestConnectionAsync called)
        provider.ApplyOptions(new ExecutionOptions { ReasoningEffort = "medium" });

        var args = provider.BuildCliArgs("test", null);

        Assert.DoesNotContain("--reasoning-effort", args);
        Assert.DoesNotContain("--effort", args);
    }

    [Fact]
    public void Copilot_SystemPromptFoldedIntoPrompt_NotAsFlag()
    {
        var provider = new CopilotProvider(CreateConfig(ProviderType.Copilot));
        provider.ApplyOptions(new ExecutionOptions
        {
            SystemPrompt = "be concise",
            AppendSystemPrompt = "extra rules"
        });

        var args = provider.BuildCliArgs("do work", null);

        // Copilot folds system prompts into the prompt text, not as CLI flags
        Assert.DoesNotContain("--system-prompt", args);
        Assert.DoesNotContain("--append-system-prompt", args);
        // The effective prompt should contain the system prompt content
        var promptIdx = args.IndexOf("-p");
        var effectivePrompt = args[promptIdx + 1];
        Assert.Contains("be concise", effectivePrompt);
        Assert.Contains("extra rules", effectivePrompt);
    }

    [Fact]
    public void Copilot_DoesNotContain_ClaudeSpecificFlags()
    {
        var provider = new CopilotProvider(CreateConfig(ProviderType.Copilot));
        provider.ApplyOptions(new ExecutionOptions
        {
            Model = "gpt-4",
            MaxTurns = 10,
            McpConfigPath = "/tmp/mcp.json",
            UseWorktree = true,
            MaxBudgetUsd = 5.00m,
            FromPullRequest = "42"
        });

        var args = provider.BuildCliArgs("test", null);

        Assert.DoesNotContain("--output-format", args);
        Assert.DoesNotContain("--verbose", args);
        Assert.DoesNotContain("--permission-mode", args);
        Assert.DoesNotContain("--worktree", args);
        Assert.DoesNotContain("--max-budget-usd", args);
        Assert.DoesNotContain("--from-pr", args);
        Assert.DoesNotContain("--max-turns", args);
        Assert.DoesNotContain("--mcp-config", args);
        Assert.DoesNotContain("--tools", args);
    }

    // ─── OpenCode Provider ─────────────────────────────────────────────

    [Fact]
    public void OpenCode_MinimalOptions_StartsWithRun()
    {
        var provider = new OpenCodeProvider(CreateConfig(ProviderType.OpenCode));
        provider.ApplyOptions(new ExecutionOptions());

        var args = provider.BuildRunCommandArgs("do something", null);

        Assert.Equal("run", args[0]);
    }

    [Fact]
    public void OpenCode_PromptIsLastArg()
    {
        var provider = new OpenCodeProvider(CreateConfig(ProviderType.OpenCode));
        provider.ApplyOptions(new ExecutionOptions { Model = "gpt-4" });

        var args = provider.BuildRunCommandArgs("my prompt", null);

        Assert.Equal("my prompt", args[^1]);
    }

    [Fact]
    public void OpenCode_WithModel_AddsModelFlag()
    {
        var provider = new OpenCodeProvider(CreateConfig(ProviderType.OpenCode));
        provider.ApplyOptions(new ExecutionOptions { Model = "anthropic/opus" });

        var args = provider.BuildRunCommandArgs("test", null);

        var idx = args.IndexOf("--model");
        Assert.True(idx >= 0);
        Assert.Equal("anthropic/opus", args[idx + 1]);
    }

    [Fact]
    public void OpenCode_WithSessionId_AddsSessionFlag()
    {
        var provider = new OpenCodeProvider(CreateConfig(ProviderType.OpenCode));
        provider.ApplyOptions(new ExecutionOptions());

        var args = provider.BuildRunCommandArgs("test", "sess-xyz");

        var idx = args.IndexOf("--session");
        Assert.True(idx >= 0);
        Assert.Equal("sess-xyz", args[idx + 1]);
    }

    [Fact]
    public void OpenCode_WithTimeout_OmitsTimeoutFlagOnAllVersions()
    {
        // The --timeout flag was removed from `opencode run` upstream;
        // VibeSwarm no longer emits it on any CLI version.
        foreach (var version in new[] { new Version(1, 2, 0), new Version(1, 3, 7), new Version(1, 4, 0) })
        {
            var provider = new OpenCodeProvider(CreateConfig(ProviderType.OpenCode));
            provider.CachedCliVersion = version;
            provider.ApplyOptions(new ExecutionOptions { TimeoutSeconds = 300 });

            var args = provider.BuildRunCommandArgs("test", null);

            Assert.DoesNotContain("--timeout", args);
        }
    }

    [Fact]
    public void OpenCode_WithReasoningEffort_AndCurrentVersion_AddsVariantFlag()
    {
        var provider = new OpenCodeProvider(CreateConfig(ProviderType.OpenCode));
        provider.CachedCliVersion = new Version(1, 3, 7);
        provider.ApplyOptions(new ExecutionOptions { ReasoningEffort = "xhigh" });

        var args = provider.BuildRunCommandArgs("test", null);

        var idx = args.IndexOf("--variant");
        Assert.True(idx >= 0);
        Assert.Equal("xhigh", args[idx + 1]);
        Assert.DoesNotContain("--reasoning", args);
        Assert.DoesNotContain("--effort", args);
        Assert.DoesNotContain("--reasoning-effort", args);
    }

    [Fact]
    public void OpenCode_WithReasoningEffort_AndPreVariantVersion_OmitsAllReasoningFlags()
    {
        // v1.2.x is no longer supported — the --reasoning flag was renamed to --variant
        // at v1.3.0. We only emit --variant on supported versions.
        var provider = new OpenCodeProvider(CreateConfig(ProviderType.OpenCode));
        provider.CachedCliVersion = new Version(1, 2, 0);
        provider.ApplyOptions(new ExecutionOptions { ReasoningEffort = "xhigh" });

        var args = provider.BuildRunCommandArgs("test", null);

        Assert.DoesNotContain("--reasoning", args);
        Assert.DoesNotContain("--variant", args);
        Assert.DoesNotContain("--effort", args);
        Assert.DoesNotContain("--reasoning-effort", args);
    }

    [Fact]
    public void OpenCode_WithReasoningEffort_AndUnknownVersion_OmitsReasoningAndVariantFlags()
    {
        var provider = new OpenCodeProvider(CreateConfig(ProviderType.OpenCode));
        provider.ApplyOptions(new ExecutionOptions { ReasoningEffort = "xhigh" });

        var args = provider.BuildRunCommandArgs("test", null);

        Assert.DoesNotContain("--reasoning", args);
        Assert.DoesNotContain("--variant", args);
        Assert.DoesNotContain("--effort", args);
        Assert.DoesNotContain("--reasoning-effort", args);
    }

    [Fact]
    public void OpenCode_WithTitle_AddsTitleFlag()
    {
        var provider = new OpenCodeProvider(CreateConfig(ProviderType.OpenCode));
        provider.ApplyOptions(new ExecutionOptions { Title = "my job" });

        var args = provider.BuildRunCommandArgs("test", null);

        var idx = args.IndexOf("--title");
        Assert.True(idx >= 0);
        Assert.Equal("my job", args[idx + 1]);
    }

    [Fact]
    public void OpenCode_WithMcpConfig_NeverEmitsConfigFlag()
    {
        // `opencode run` does not accept --config; MCP servers are configured via
        // opencode.json(c) in the working dir. VibeSwarm must not emit --config.
        var provider = new OpenCodeProvider(CreateConfig(ProviderType.OpenCode));
        provider.CachedCliVersion = new Version(1, 4, 0);
        provider.ApplyOptions(new ExecutionOptions { McpConfigPath = "/tmp/config.json" });

        var args = provider.BuildRunCommandArgs("test", null);

        Assert.DoesNotContain("--config", args);
        Assert.DoesNotContain("--mcp-config", args);
        Assert.DoesNotContain("--additional-mcp-config", args);
    }

    [Fact]
    public void OpenCode_WithAdditionalDirectory_AndSupportedVersion_AddsDirFlag()
    {
        var provider = new OpenCodeProvider(CreateConfig(ProviderType.OpenCode));
        provider.CachedCliVersion = new Version(1, 2, 0);
        provider.ApplyOptions(new ExecutionOptions { AdditionalDirectories = ["/tmp/worktree"] });

        var args = provider.BuildRunCommandArgs("test", null);

        var idx = args.IndexOf("--dir");
        Assert.True(idx >= 0);
        Assert.Equal("/tmp/worktree", args[idx + 1]);
    }

    [Fact]
    public void OpenCode_WithAdditionalDirectory_AndUnknownVersion_OmitsDirFlag()
    {
        var provider = new OpenCodeProvider(CreateConfig(ProviderType.OpenCode));
        provider.ApplyOptions(new ExecutionOptions { AdditionalDirectories = ["/tmp/worktree"] });

        var args = provider.BuildRunCommandArgs("test", null);

        Assert.DoesNotContain("--dir", args);
    }

    [Fact]
    public void OpenCode_WithAdditionalDirectory_AndCurrentVersion_AddsDirFlag()
    {
        var provider = new OpenCodeProvider(CreateConfig(ProviderType.OpenCode));
        provider.CachedCliVersion = new Version(1, 3, 7);
        provider.ApplyOptions(new ExecutionOptions { AdditionalDirectories = ["/tmp/worktree"] });

        var args = provider.BuildRunCommandArgs("test", null);

        var idx = args.IndexOf("--dir");
        Assert.True(idx >= 0);
        Assert.Equal("/tmp/worktree", args[idx + 1]);
    }

	[Fact]
	public void OpenCode_WithForkSession_AndSupportedVersion_AddsForkFlag()
	{
		var provider = new OpenCodeProvider(CreateConfig(ProviderType.OpenCode));
		provider.CachedCliVersion = new Version(1, 2, 6);
        provider.ApplyOptions(new ExecutionOptions { ForkSession = true });

        var args = provider.BuildRunCommandArgs("test", "sess-xyz");

		Assert.Contains("--fork", args);
	}

	[Fact]
	public void OpenCode_WithForkSession_AndContinueLastSession_AddsForkFlag()
	{
		var provider = new OpenCodeProvider(CreateConfig(ProviderType.OpenCode));
		provider.CachedCliVersion = new Version(1, 3, 13);
		provider.ApplyOptions(new ExecutionOptions
		{
			ContinueLastSession = true,
			ForkSession = true
		});

		var args = provider.BuildRunCommandArgs("test", null);

		Assert.Contains("--continue", args);
		Assert.Contains("--fork", args);
	}

	[Fact]
	public void OpenCode_WithForkSession_AndOldVersion_OmitsForkFlag()
	{
		var provider = new OpenCodeProvider(CreateConfig(ProviderType.OpenCode));
		provider.CachedCliVersion = new Version(1, 2, 5);
		provider.ApplyOptions(new ExecutionOptions { ForkSession = true });

        var args = provider.BuildRunCommandArgs("test", "sess-xyz");

        Assert.DoesNotContain("--fork", args);
    }

    [Fact]
    public void OpenCode_BuildUpdatePlan_ForUserLocalInstall_UsesNpmMethodAndPrefix()
    {
        var plan = OpenCodeProvider.BuildUpdatePlan("/home/test/.local/bin/opencode", "/home/test");

        Assert.Equal("upgrade --method npm", plan.Arguments);
        Assert.Equal("/home/test/.local", plan.NpmConfigPrefix);
    }

    [Fact]
    public void OpenCode_BuildUpdatePlan_ForNestedUserLocalInstall_UsesNpmMethodAndPrefix()
    {
        var plan = OpenCodeProvider.BuildUpdatePlan(
            "/home/test/.local/lib/node_modules/opencode-ai/bin/.opencode",
            "/home/test");

        Assert.Equal("upgrade --method npm", plan.Arguments);
        Assert.Equal("/home/test/.local", plan.NpmConfigPrefix);
    }

    [Fact]
    public void OpenCode_DoesNotContain_CopilotOrClaudeFlags()
    {
        var provider = new OpenCodeProvider(CreateConfig(ProviderType.OpenCode));
        provider.ApplyOptions(new ExecutionOptions
        {
            Model = "gpt-4",
            MaxTurns = 10,
            McpConfigPath = "/tmp/mcp.json"
        });

        var args = provider.BuildRunCommandArgs("test", null);

        Assert.DoesNotContain("--yolo", args);
        Assert.DoesNotContain("--silent", args);
        Assert.DoesNotContain("--autopilot", args);
        Assert.DoesNotContain("--bash-env", args);
        Assert.DoesNotContain("--no-mouse", args);
        Assert.DoesNotContain("--output-format", args);
        Assert.DoesNotContain("--verbose", args);
        Assert.DoesNotContain("--permission-mode", args);
        Assert.DoesNotContain("--worktree", args);
        Assert.DoesNotContain("--max-budget-usd", args);
        Assert.DoesNotContain("--from-pr", args);
        Assert.DoesNotContain("--tools", args);
        Assert.DoesNotContain("--disallowed-tools", args);
        Assert.DoesNotContain("--available-tools", args);
        Assert.DoesNotContain("--excluded-tools", args);
        Assert.DoesNotContain("--deny-tool", args);
    }

    // ─── Cross-provider flag format validation ─────────────────────────

    [Theory]
    [InlineData("--model")]
    [InlineData("--resume")]
    [InlineData("--max-turns")]
    [InlineData("--system-prompt")]
    [InlineData("--mcp-config")]
    [InlineData("--effort")]
    public void Claude_FlagsThatTakeValues_AreFollowedByValue(string flag)
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));
        provider.ApplyOptions(new ExecutionOptions
        {
            Model = "opus",
            MaxTurns = 10,
            SystemPrompt = "test",
            McpConfigPath = "/tmp/mcp.json",
            ReasoningEffort = "high"
        });

        var args = provider.BuildCliArgs("test", "session-1");
        var idx = args.IndexOf(flag);
        if (idx >= 0)
        {
            // Value follows the flag as separate arg (not --flag=value)
            Assert.True(idx + 1 < args.Count, $"Flag {flag} has no following value");
            Assert.False(args[idx].Contains('='), $"Flag {flag} should not use = syntax");
        }
    }

    // ─── Claude: new Tier 1 flags ──────────────────────────────────────

    [Fact]
    public void Claude_WithPreassignedSessionId_AndSupportedVersion_AddsSessionIdFlag()
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));
        provider.CachedCliVersion = new Version(2, 1, 108);
        provider.ApplyOptions(new ExecutionOptions { PreassignedSessionId = "abc-123" });

        var args = provider.BuildCliArgs("test", null);

        var idx = args.IndexOf("--session-id");
        Assert.True(idx >= 0);
        Assert.Equal("abc-123", args[idx + 1]);
        Assert.DoesNotContain("--resume", args);
    }

    [Fact]
    public void Claude_WithPreassignedSessionId_AndOldVersion_OmitsSessionIdFlag()
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));
        provider.CachedCliVersion = new Version(2, 1, 80);
        provider.ApplyOptions(new ExecutionOptions { PreassignedSessionId = "abc-123" });

        var args = provider.BuildCliArgs("test", null);

        Assert.DoesNotContain("--session-id", args);
    }

    [Fact]
    public void Claude_WithResumeSession_IgnoresPreassignedSessionId()
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));
        provider.CachedCliVersion = new Version(2, 1, 108);
        provider.ApplyOptions(new ExecutionOptions { PreassignedSessionId = "fresh-id" });

        var args = provider.BuildCliArgs("test", "existing-id");

        // Resume wins over preassigned; --session-id should NOT be added.
        Assert.Contains("--resume", args);
        Assert.DoesNotContain("--session-id", args);
    }

    [Fact]
    public void Claude_WithFallbackModel_AddsFallbackModelFlag()
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));
        provider.CachedCliVersion = new Version(2, 1, 100);
        provider.ApplyOptions(new ExecutionOptions { FallbackModel = "haiku" });

        var args = provider.BuildCliArgs("test", null);

        var idx = args.IndexOf("--fallback-model");
        Assert.True(idx >= 0);
        Assert.Equal("haiku", args[idx + 1]);
    }

    [Fact]
    public void Claude_WithStrictMcpConfig_AddsStrictMcpFlag()
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));
        provider.CachedCliVersion = new Version(2, 1, 100);
        provider.ApplyOptions(new ExecutionOptions
        {
            McpConfigPath = "/tmp/mcp.json",
            StrictMcpConfig = true
        });

        var args = provider.BuildCliArgs("test", null);

        Assert.Contains("--strict-mcp-config", args);
    }

    [Fact]
    public void Claude_WithStrictMcpConfig_WithoutMcpPath_OmitsStrictMcpFlag()
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));
        provider.CachedCliVersion = new Version(2, 1, 100);
        provider.ApplyOptions(new ExecutionOptions { StrictMcpConfig = true });

        var args = provider.BuildCliArgs("test", null);

        // --strict-mcp-config is only meaningful when --mcp-config is present.
        Assert.DoesNotContain("--strict-mcp-config", args);
    }

    [Fact]
    public void Claude_WithSettingSources_AddsFlag()
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));
        provider.CachedCliVersion = new Version(2, 1, 100);
        provider.ApplyOptions(new ExecutionOptions { SettingSources = "project" });

        var args = provider.BuildCliArgs("test", null);

        var idx = args.IndexOf("--setting-sources");
        Assert.True(idx >= 0);
        Assert.Equal("project", args[idx + 1]);
    }

    [Fact]
    public void Claude_WithExcludeDynamicSystemPromptSections_AndSupportedVersion_AddsFlag()
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));
        provider.CachedCliVersion = new Version(2, 1, 98);
        provider.ApplyOptions(new ExecutionOptions { ExcludeDynamicSystemPromptSections = true });

        var args = provider.BuildCliArgs("test", null);

        Assert.Contains("--exclude-dynamic-system-prompt-sections", args);
    }

    [Fact]
    public void Claude_WithExcludeDynamicSystemPromptSections_AndOldVersion_OmitsFlag()
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));
        provider.CachedCliVersion = new Version(2, 1, 97);
        provider.ApplyOptions(new ExecutionOptions { ExcludeDynamicSystemPromptSections = true });

        var args = provider.BuildCliArgs("test", null);

        Assert.DoesNotContain("--exclude-dynamic-system-prompt-sections", args);
    }

    [Fact]
    public void Claude_WithNoSessionPersistence_AddsFlag()
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));
        provider.CachedCliVersion = new Version(2, 1, 100);
        provider.ApplyOptions(new ExecutionOptions { NoSessionPersistence = true });

        var args = provider.BuildCliArgs("test", null);

        Assert.Contains("--no-session-persistence", args);
    }

    [Fact]
    public void Claude_WithSessionName_AddsNameFlag()
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));
        provider.CachedCliVersion = new Version(2, 1, 100);
        provider.ApplyOptions(new ExecutionOptions { SessionName = "job-42" });

        var args = provider.BuildCliArgs("test", null);

        var idx = args.IndexOf("--name");
        Assert.True(idx >= 0);
        Assert.Equal("job-42", args[idx + 1]);
    }

    [Fact]
    public void Claude_WithIncludeHookEvents_AddsFlag()
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));
        provider.CachedCliVersion = new Version(2, 1, 100);
        provider.ApplyOptions(new ExecutionOptions { IncludeHookEvents = true });

        var args = provider.BuildCliArgs("test", null);

        Assert.Contains("--include-hook-events", args);
    }

    [Fact]
    public void Claude_WithAppendSystemPromptFile_AddsFlag()
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));
        provider.CachedCliVersion = new Version(2, 1, 100);
        provider.ApplyOptions(new ExecutionOptions { AppendSystemPromptFile = "/tmp/prompt.md" });

        var args = provider.BuildCliArgs("test", null);

        var idx = args.IndexOf("--append-system-prompt-file");
        Assert.True(idx >= 0);
        Assert.Equal("/tmp/prompt.md", args[idx + 1]);
    }

    [Fact]
    public void Claude_WithForkSession_AndResume_AddsForkSessionFlag()
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));
        provider.CachedCliVersion = new Version(2, 1, 100);
        provider.ApplyOptions(new ExecutionOptions { ForkSession = true });

        var args = provider.BuildCliArgs("test", "parent-session");

        Assert.Contains("--resume", args);
        Assert.Contains("--fork-session", args);
    }

    [Fact]
    public void Claude_WithForkSession_WithoutResumeOrContinue_OmitsForkSessionFlag()
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));
        provider.CachedCliVersion = new Version(2, 1, 100);
        provider.ApplyOptions(new ExecutionOptions { ForkSession = true });

        var args = provider.BuildCliArgs("test", null);

        Assert.DoesNotContain("--fork-session", args);
    }

    [Fact]
    public void Claude_WithJsonSchema_AndJsonOutputFormat_AddsSchemaFlag()
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));
        provider.CachedCliVersion = new Version(2, 1, 100);
        provider.ApplyOptions(new ExecutionOptions
        {
            JsonSchema = "{\"type\":\"object\"}",
            OutputFormat = "json"
        });

        var args = provider.BuildCliArgs("test", null);

        var idx = args.IndexOf("--json-schema");
        Assert.True(idx >= 0);
        Assert.Equal("{\"type\":\"object\"}", args[idx + 1]);
    }

    [Fact]
    public void Claude_WithJsonSchema_WithoutJsonOutputFormat_OmitsSchemaFlag()
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));
        provider.CachedCliVersion = new Version(2, 1, 100);
        provider.ApplyOptions(new ExecutionOptions { JsonSchema = "{\"type\":\"object\"}" });

        var args = provider.BuildCliArgs("test", null);

        // --json-schema only emitted when the caller has set OutputFormat = "json".
        Assert.DoesNotContain("--json-schema", args);
    }

    // ─── Copilot: new Tier 1 / Tier 2 flags ────────────────────────────

    [Fact]
    public void Copilot_DefaultMode_StillEmitsYoloAutopilot()
    {
        var provider = new CopilotProvider(CreateConfig(ProviderType.Copilot));
        provider.CachedCliVersion = new Version(1, 0, 27);
        provider.ApplyOptions(new ExecutionOptions());

        var args = provider.BuildCliArgs("test", null);

        Assert.Contains("--yolo", args);
        Assert.Contains("--autopilot", args);
        Assert.DoesNotContain("--mode", args);
    }

    [Fact]
    public void Copilot_WithExplicitMode_EmitsModeFlagInsteadOfYolo()
    {
        var provider = new CopilotProvider(CreateConfig(ProviderType.Copilot));
        provider.CachedCliVersion = new Version(1, 0, 27);
        provider.ApplyOptions(new ExecutionOptions { CopilotMode = "plan" });

        var args = provider.BuildCliArgs("test", null);

        var idx = args.IndexOf("--mode");
        Assert.True(idx >= 0);
        Assert.Equal("plan", args[idx + 1]);
        Assert.DoesNotContain("--yolo", args);
        // Still needs headless permission flags.
        Assert.Contains("--allow-all-tools", args);
        Assert.Contains("--allow-all-paths", args);
    }

    [Fact]
    public void Copilot_WithDisableCustomInstructions_AddsFlag()
    {
        var provider = new CopilotProvider(CreateConfig(ProviderType.Copilot));
        provider.CachedCliVersion = new Version(1, 0, 27);
        provider.ApplyOptions(new ExecutionOptions { DisableCustomInstructions = true });

        var args = provider.BuildCliArgs("test", null);

        Assert.Contains("--no-custom-instructions", args);
    }

    [Fact]
    public void Copilot_WithDisableAskUser_AddsFlag()
    {
        var provider = new CopilotProvider(CreateConfig(ProviderType.Copilot));
        provider.CachedCliVersion = new Version(1, 0, 27);
        provider.ApplyOptions(new ExecutionOptions { DisableAskUser = true });

        var args = provider.BuildCliArgs("test", null);

        Assert.Contains("--no-ask-user", args);
    }

    [Fact]
    public void Copilot_WithDisableBuiltinMcps_AddsFlag()
    {
        var provider = new CopilotProvider(CreateConfig(ProviderType.Copilot));
        provider.CachedCliVersion = new Version(1, 0, 27);
        provider.ApplyOptions(new ExecutionOptions { DisableBuiltinMcps = true });

        var args = provider.BuildCliArgs("test", null);

        Assert.Contains("--disable-builtin-mcps", args);
    }

    [Fact]
    public void Copilot_WithDisabledMcpServers_AddsRepeatedFlags()
    {
        var provider = new CopilotProvider(CreateConfig(ProviderType.Copilot));
        provider.CachedCliVersion = new Version(1, 0, 27);
        provider.ApplyOptions(new ExecutionOptions
        {
            DisabledMcpServers = new List<string> { "github", "jira" }
        });

        var args = provider.BuildCliArgs("test", null);

        var count = args.Count(a => a == "--disable-mcp-server");
        Assert.Equal(2, count);
        Assert.Contains("github", args);
        Assert.Contains("jira", args);
    }

    [Fact]
    public void Copilot_WithAllowedAndDeniedUrls_AddsFlags()
    {
        var provider = new CopilotProvider(CreateConfig(ProviderType.Copilot));
        provider.CachedCliVersion = new Version(1, 0, 27);
        provider.ApplyOptions(new ExecutionOptions
        {
            AllowedUrls = new List<string> { "api.github.com" },
            DeniedUrls = new List<string> { "evil.example.com" }
        });

        var args = provider.BuildCliArgs("test", null);

        var allowIdx = args.IndexOf("--allow-url");
        var denyIdx = args.IndexOf("--deny-url");
        Assert.True(allowIdx >= 0);
        Assert.Equal("api.github.com", args[allowIdx + 1]);
        Assert.True(denyIdx >= 0);
        Assert.Equal("evil.example.com", args[denyIdx + 1]);
    }

    [Fact]
    public void Copilot_WithStreamOutputTrue_EmitsStreamOn()
    {
        var provider = new CopilotProvider(CreateConfig(ProviderType.Copilot));
        provider.CachedCliVersion = new Version(1, 0, 27);
        provider.ApplyOptions(new ExecutionOptions { StreamOutput = true });

        var args = provider.BuildCliArgs("test", null);

        var idx = args.IndexOf("--stream");
        Assert.True(idx >= 0);
        Assert.Equal("on", args[idx + 1]);
    }

    [Fact]
    public void Copilot_WithStreamOutputFalse_EmitsStreamOff()
    {
        var provider = new CopilotProvider(CreateConfig(ProviderType.Copilot));
        provider.CachedCliVersion = new Version(1, 0, 27);
        provider.ApplyOptions(new ExecutionOptions { StreamOutput = false });

        var args = provider.BuildCliArgs("test", null);

        var idx = args.IndexOf("--stream");
        Assert.True(idx >= 0);
        Assert.Equal("off", args[idx + 1]);
    }

    [Fact]
    public void Copilot_WithApiKey_AutoIncludesProviderTokensInSecretEnvVars()
    {
        var config = CreateConfig(ProviderType.Copilot);
        config.ApiKey = "gho_testtoken";
        var provider = new CopilotProvider(config);
        provider.CachedCliVersion = new Version(1, 0, 27);
        provider.ApplyOptions(new ExecutionOptions());

        var args = provider.BuildCliArgs("test", null);

        var idx = args.IndexOf("--secret-env-vars");
        Assert.True(idx >= 0);
        var value = args[idx + 1];
        // GH_TOKEN and GITHUB_TOKEN are auto-injected by the provider.
        Assert.Contains("GH_TOKEN", value);
        Assert.Contains("GITHUB_TOKEN", value);
    }

    [Fact]
    public void Copilot_WithoutSecrets_OmitsSecretEnvVarsFlag()
    {
        var provider = new CopilotProvider(CreateConfig(ProviderType.Copilot));
        provider.CachedCliVersion = new Version(1, 0, 27);
        provider.ApplyOptions(new ExecutionOptions());

        var args = provider.BuildCliArgs("test", null);

        Assert.DoesNotContain("--secret-env-vars", args);
    }

    // ─── OpenCode: new Tier 1 flags ────────────────────────────────────

    [Fact]
    public void OpenCode_WithSkipPermissions_AndSupportedVersion_AddsFlag()
    {
        var provider = new OpenCodeProvider(CreateConfig(ProviderType.OpenCode));
        provider.CachedCliVersion = new Version(1, 4, 0);
        provider.ApplyOptions(new ExecutionOptions { SkipPermissions = true });

        var args = provider.BuildRunCommandArgs("test", null);

        Assert.Contains("--dangerously-skip-permissions", args);
    }

    [Fact]
    public void OpenCode_WithSkipPermissions_AndOldVersion_OmitsFlag()
    {
        var provider = new OpenCodeProvider(CreateConfig(ProviderType.OpenCode));
        provider.CachedCliVersion = new Version(1, 3, 99);
        provider.ApplyOptions(new ExecutionOptions { SkipPermissions = true });

        var args = provider.BuildRunCommandArgs("test", null);

        Assert.DoesNotContain("--dangerously-skip-permissions", args);
    }

    [Fact]
    public void OpenCode_WithShowThinking_AndSupportedVersion_AddsFlag()
    {
        var provider = new OpenCodeProvider(CreateConfig(ProviderType.OpenCode));
        provider.CachedCliVersion = new Version(1, 4, 0);
        provider.ApplyOptions(new ExecutionOptions { ShowThinking = true });

        var args = provider.BuildRunCommandArgs("test", null);

        Assert.Contains("--thinking", args);
    }
}
