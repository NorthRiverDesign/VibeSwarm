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
