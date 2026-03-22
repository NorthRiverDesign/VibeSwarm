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
        provider.ApplyOptions(new ExecutionOptions { ReasoningEffort = "high" });

        var args = provider.BuildCliArgs("test", null);

        var idx = args.IndexOf("--effort");
        Assert.True(idx >= 0);
        Assert.Equal("high", args[idx + 1]);
    }

    [Fact]
    public void Claude_WithMaxBudget_AddsMaxBudgetFlag()
    {
        var provider = new ClaudeProvider(CreateConfig(ProviderType.Claude));
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
        provider.ApplyOptions(new ExecutionOptions { FromPullRequest = "42" });

        var args = provider.BuildCliArgs("test", null);

        var idx = args.IndexOf("--from-pr");
        Assert.True(idx >= 0);
        Assert.Equal("42", args[idx + 1]);
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
    public void Copilot_WithBashEnvPath_AddsBashEnvFlag()
    {
        var provider = new CopilotProvider(CreateConfig(ProviderType.Copilot));
        provider.ApplyOptions(new ExecutionOptions { BashEnvPath = "/tmp/bash-env.sh" });

        var args = provider.BuildCliArgs("test", null);

        var idx = args.IndexOf("--bash-env");
        Assert.True(idx >= 0);
        Assert.Equal("/tmp/bash-env.sh", args[idx + 1]);
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
        provider.ApplyOptions(new ExecutionOptions { UseAltScreen = true });

        var args = provider.BuildCliArgs("test", null);

        var idx = args.IndexOf("--alt-screen");
        Assert.True(idx >= 0);
        Assert.Equal("on", args[idx + 1]);
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
    public void Copilot_WithReasoningEffort_AddsReasoningEffortFlag()
    {
        var provider = new CopilotProvider(CreateConfig(ProviderType.Copilot));
        provider.ApplyOptions(new ExecutionOptions { ReasoningEffort = "medium" });

        var args = provider.BuildCliArgs("test", null);

        var idx = args.IndexOf("--reasoning-effort");
        Assert.True(idx >= 0);
        Assert.Equal("medium", args[idx + 1]);
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
    public void OpenCode_WithTimeout_AddsTimeoutFlag()
    {
        var provider = new OpenCodeProvider(CreateConfig(ProviderType.OpenCode));
        provider.ApplyOptions(new ExecutionOptions { TimeoutSeconds = 300 });

        var args = provider.BuildRunCommandArgs("test", null);

        var idx = args.IndexOf("--timeout");
        Assert.True(idx >= 0);
        Assert.Equal("300", args[idx + 1]);
    }

    [Fact]
    public void OpenCode_WithReasoningEffort_AddsReasoningFlag()
    {
        var provider = new OpenCodeProvider(CreateConfig(ProviderType.OpenCode));
        provider.ApplyOptions(new ExecutionOptions { ReasoningEffort = "xhigh" });

        var args = provider.BuildRunCommandArgs("test", null);

        var idx = args.IndexOf("--reasoning");
        Assert.True(idx >= 0);
        Assert.Equal("xhigh", args[idx + 1]);
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
    public void OpenCode_WithMcpConfig_AddsConfigFlag()
    {
        var provider = new OpenCodeProvider(CreateConfig(ProviderType.OpenCode));
        provider.ApplyOptions(new ExecutionOptions { McpConfigPath = "/tmp/config.json" });

        var args = provider.BuildRunCommandArgs("test", null);

        var idx = args.IndexOf("--config");
        Assert.True(idx >= 0);
        Assert.Equal("/tmp/config.json", args[idx + 1]);
        Assert.DoesNotContain("--mcp-config", args);
        Assert.DoesNotContain("--additional-mcp-config", args);
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
}
