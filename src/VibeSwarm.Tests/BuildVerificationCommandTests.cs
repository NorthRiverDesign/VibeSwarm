using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class BuildVerificationCommandTests
{
	[Fact]
	public async Task RunShellCommandAsync_HandsTheWholeCommandToBash()
	{
		var workingDirectory = Path.Combine(Path.GetTempPath(), "vibeswarm-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(workingDirectory);

		try
		{
			var (exitCode, output, error) = await JobProcessingService.RunShellCommandAsync(
				"printf '%s|' 'two words' \"$(basename \"$PWD\")\"; exit 3",
				workingDirectory,
				CancellationToken.None);

			Assert.Equal(3, exitCode);
			Assert.Equal($"two words|{Path.GetFileName(workingDirectory)}|", output);
			Assert.Equal(string.Empty, error);
		}
		finally
		{
			Directory.Delete(workingDirectory, recursive: true);
		}
	}

	[Fact]
	public async Task RunTestCommandAsync_RerunsAFailedTestCommand_AndPassesWhenTheRerunPasses()
	{
		var workingDirectory = Path.Combine(Path.GetTempPath(), "vibeswarm-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(workingDirectory);

		try
		{
			// Fails the first time only, like a timing-sensitive test on a busy machine.
			var output = new System.Text.StringBuilder();
			var (exitCode, runs) = await JobProcessingService.RunTestCommandAsync(
				"if [ -f ran ]; then echo second-run-passed; else touch ran; echo first-run-failed; exit 1; fi",
				workingDirectory,
				output,
				CancellationToken.None);

			Assert.Equal(0, exitCode);
			Assert.Equal(2, runs);
			Assert.Contains("first-run-failed", output.ToString());
			Assert.Contains("second-run-passed", output.ToString());
			Assert.Contains("re-run after a failure", output.ToString());
		}
		finally
		{
			Directory.Delete(workingDirectory, recursive: true);
		}
	}

	[Fact]
	public async Task RunTestCommandAsync_FailsWhenTheTestCommandFailsTwice_AndRunsAPassingCommandOnce()
	{
		var workingDirectory = Path.Combine(Path.GetTempPath(), "vibeswarm-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(workingDirectory);

		try
		{
			var failing = await JobProcessingService.RunTestCommandAsync("exit 4", workingDirectory, new System.Text.StringBuilder(), CancellationToken.None);
			Assert.Equal((4, 2), failing);

			var passing = await JobProcessingService.RunTestCommandAsync("true", workingDirectory, new System.Text.StringBuilder(), CancellationToken.None);
			Assert.Equal((0, 1), passing);
		}
		finally
		{
			Directory.Delete(workingDirectory, recursive: true);
		}
	}

	[Fact]
	public void FindVerificationFailureLines_KeepsWhatFailedFromBothRuns()
	{
		var run = string.Join('\n',
			"  Determining projects to restore...",
			"/src/Tests/GitTests.cs(288,66): warning CS8604: Possible null reference argument [/src/Tests/Tests.csproj]",
			"[xUnit.net 00:00:05.12]     VibeSwarm.Tests.GitTests.Push_Works [FAIL]",
			"  Failed VibeSwarm.Tests.GitTests.Push_Works [212 ms]",
			"  Error Message:",
			"   Assert.Equal() Failure: Values differ",
			"The active test run was aborted. Reason: Test host process crashed",
			"Failed!  - Failed:     1, Passed:  1346, Skipped:     0, Total:  1347, Duration: 58 s - VibeSwarm.Tests.dll (net10.0)");
		var output = $"=== Build Command: dotnet build ===\n/src/Web/App.cs(10,5): error CS1002: ; expected [/src/Web/Web.csproj]\n{run}\n{run}";

		var lines = JobProcessingService.FindVerificationFailureLines(output);

		Assert.Equal(
			[
				"/src/Web/App.cs(10,5): error CS1002: ; expected [/src/Web/Web.csproj]",
				"Failed VibeSwarm.Tests.GitTests.Push_Works [212 ms]",
				"The active test run was aborted. Reason: Test host process crashed",
				"Failed!  - Failed:     1, Passed:  1346, Skipped:     0, Total:  1347, Duration: 58 s - VibeSwarm.Tests.dll (net10.0)"
			],
			lines);
		Assert.Single(JobProcessingService.FindVerificationFailureLines(output, maxLines: 1));
	}

	[Fact]
	public void RemoveVibeSwarmDatabaseSettings_KeepsEverythingButTheServiceDatabaseConfiguration()
	{
		var environment = new Dictionary<string, string?>
		{
			["DATABASE_PROVIDER"] = "mysql",
			["ConnectionStrings__Default"] = "Server=localhost;Database=vibeswarm",
			["ConnectionStrings__Reporting"] = "Server=localhost;Database=reports",
			["PATH"] = "/usr/bin",
			["HOME"] = "/home/pi"
		};

		JobProcessingService.RemoveVibeSwarmDatabaseSettings(environment);

		Assert.Equal(["HOME", "PATH"], environment.Keys.Order(StringComparer.Ordinal).ToArray());
	}
}
