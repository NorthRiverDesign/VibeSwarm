using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

public sealed class CliProviderBaseTests
{
	[Fact]
	public void ReportProcessStarted_TracksProcessWithoutStartupActivityMessage()
	{
		var provider = new TestCliProvider();
		var progressUpdates = new List<ExecutionProgress>();

		provider.EmitProcessStarted(123, new RecordingProgress(progressUpdates), "copilot -p \"test\"");

		Assert.Collection(progressUpdates,
			metadata =>
			{
				Assert.Equal(123, metadata.ProcessId);
				Assert.Equal("copilot -p \"test\"", metadata.CommandUsed);
				Assert.Null(metadata.CurrentMessage);
				Assert.Null(metadata.OutputLine);
				Assert.False(metadata.IsStreaming);
			},
			output =>
			{
				Assert.Null(output.ProcessId);
				Assert.Equal("[System] Process started (PID: 123). Waiting for CLI to initialize...", output.OutputLine);
				Assert.True(output.IsStreaming);
			});
	}

	private sealed class RecordingProgress(List<ExecutionProgress> updates) : IProgress<ExecutionProgress>
	{
		public void Report(ExecutionProgress value) => updates.Add(value);
	}

	private sealed class TestCliProvider() : CliProviderBase(
		Guid.NewGuid(),
		"Test CLI",
		ProviderConnectionMode.CLI,
		"test-cli",
		null)
	{
		public override ProviderType Type => ProviderType.Claude;

		public void EmitProcessStarted(int processId, IProgress<ExecutionProgress>? progress, string? fullCommand = null)
			=> ReportProcessStarted(processId, progress, fullCommand);

		public override Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
			=> throw new NotSupportedException();

		public override Task<string> ExecuteAsync(string prompt, CancellationToken cancellationToken = default)
			=> throw new NotSupportedException();

		public override Task<ExecutionResult> ExecuteWithSessionAsync(
			string prompt,
			string? sessionId = null,
			string? workingDirectory = null,
			IProgress<ExecutionProgress>? progress = null,
			CancellationToken cancellationToken = default)
			=> throw new NotSupportedException();

		public override Task<ProviderInfo> GetProviderInfoAsync(CancellationToken cancellationToken = default)
			=> throw new NotSupportedException();

		public override Task<UsageLimits> GetUsageLimitsAsync(CancellationToken cancellationToken = default)
			=> throw new NotSupportedException();

		public override Task<SessionSummary> GetSessionSummaryAsync(
			string? sessionId,
			string? workingDirectory = null,
			string? fallbackOutput = null,
			CancellationToken cancellationToken = default)
			=> throw new NotSupportedException();

		public override Task<PromptResponse> GetPromptResponseAsync(
			string prompt,
			string? workingDirectory = null,
			CancellationToken cancellationToken = default)
			=> throw new NotSupportedException();

		public override Task<CliUpdateResult> UpdateCliAsync(CancellationToken cancellationToken = default)
			=> throw new NotSupportedException();
	}
}
