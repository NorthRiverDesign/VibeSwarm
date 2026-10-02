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
