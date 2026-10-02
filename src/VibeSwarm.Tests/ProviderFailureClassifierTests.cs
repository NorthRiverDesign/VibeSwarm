using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

public class ProviderFailureClassifierTests
{
	[Theory]
	[InlineData("error: bubblewrap is required for subprocess env scrubbing and isolation.")]
	[InlineData("\u26a0 Permission mode forced to default \u2014 CLAUDE_CODE_SUBPROCESS_ENV_SCRUB is set (allowed_non_write_users hardening).")]
	[InlineData("claude: command not found")]
	[InlineData("Failed to start Claude CLI process: No such file or directory")]
	[InlineData("error: unknown option '--exclude-dynamic-system-prompt-sections'")]
	[InlineData("Invalid API key · Please run /login")]
	public void Classify_HostAndConfigurationFaults_AreUnrecoverable(string errorText)
	{
		Assert.Equal(ProviderFailureKind.Unrecoverable, ProviderFailureClassifier.Classify(errorText));
	}

	[Theory]
	[InlineData("API Error: 529 upstream service temporarily unavailable")]
	[InlineData("Claude usage limit reached. Your limit will reset at 9pm.")]
	[InlineData("Request timed out after 600s")]
	[InlineData("")]
	[InlineData(null)]
	public void Classify_ProviderIssuedErrors_StayRetryable(string? errorText)
	{
		Assert.Equal(ProviderFailureKind.Retryable, ProviderFailureClassifier.Classify(errorText));
	}

	[Fact]
	public void Summarize_PullsTheErrorLineOutOfABundledStackTrace()
	{
		// What the Claude CLI actually prints when it aborts at startup: a code frame from
		// its own bundle, then the reason on the last line.
		var raw = string.Join('\n',
		[
			"  6 | // and may be used to improve Anthropic's products, including training models.",
			"  7 | // You are responsible for reviewing any code suggestions before use.",
			" 11 | import{Me,vo}from\"/$bunfs/root/chunk-qxq6ebfp.js\";",
			"      ^",
			"error: bubblewrap is required for subprocess env scrubbing and isolation. Install with: sudo apt-get install -y bubblewrap",
			"      at async <anonymous> (/$bunfs/root/chunk-g2rzvvq6.js:91:5443)",
			"",
			"Bun v1.4.3 (Linux arm64)"
		]);

		var summary = ProviderFailureClassifier.Summarize(raw);

		Assert.StartsWith("error: bubblewrap is required", summary);
		Assert.DoesNotContain("$bunfs", summary);
		Assert.DoesNotContain("Bun v1.4.3", summary);
	}

	[Fact]
	public void Summarize_WithoutAnErrorLine_UsesTheLastMeaningfulLine()
	{
		var summary = ProviderFailureClassifier.Summarize("warming up\nsomething went sideways\n  at async run()\n");

		Assert.Equal("something went sideways", summary);
	}

	[Fact]
	public void Summarize_LongOutput_IsTruncated()
	{
		var summary = ProviderFailureClassifier.Summarize(new string('x', 2000));

		Assert.NotNull(summary);
		Assert.True(summary!.Length <= 601, $"Summary was {summary.Length} characters.");
		Assert.EndsWith("…", summary);
	}

	[Fact]
	public void Summarize_EmptyInput_ReturnsNull()
	{
		Assert.Null(ProviderFailureClassifier.Summarize("   "));
	}
}
