using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Utilities;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class ProviderCliDetectionServiceTests : IDisposable
{
	private readonly string _tempDirectory;
	private readonly string _binDirectory;
	private readonly string _homeDirectory;
	private readonly ProviderCliDetectionService _service = new(NullLogger<ProviderCliDetectionService>.Instance);

	public ProviderCliDetectionServiceTests()
	{
		_tempDirectory = Path.Combine(Path.GetTempPath(), "vibeswarm-provider-detection-tests", Guid.NewGuid().ToString("N"));
		_binDirectory = Path.Combine(_tempDirectory, "bin");
		_homeDirectory = Path.Combine(_tempDirectory, "home");

		Directory.CreateDirectory(_binDirectory);
		Directory.CreateDirectory(_homeDirectory);
	}

	[Fact]
	public async Task DetectAsync_CopilotUsesBinaryVersionAndEnhancedUserPath()
	{
		var localBinDirectory = Path.Combine(_homeDirectory, ".local", "bin");
		Directory.CreateDirectory(localBinDirectory);

		var executablePath = Path.Combine(localBinDirectory, "copilot");
		await File.WriteAllTextAsync(executablePath, """
			#!/bin/sh
			if [ "$1" = "--binary-version" ]; then
				echo "copilot 1.2.3"
				exit 0
			fi
			echo "wrong args: $1" >&2
			exit 1
			""");
		MakeExecutable(executablePath);

		var result = await _service.DetectAsync(
			ProviderType.Copilot,
			"copilot",
			"--binary-version",
			homeDirectory: _homeDirectory,
			searchPath: PlatformHelper.GetEnhancedPath(_homeDirectory));

		Assert.True(result.IsInstalled);
		Assert.Equal("copilot 1.2.3", result.Version);
		Assert.Equal(executablePath, result.ResolvedExecutablePath);
	}

	[Fact]
	public async Task DetectAsync_UsesStderrVersionOutputWhenAvailable()
	{
		var executablePath = Path.Combine(_binDirectory, "claude");
		await File.WriteAllTextAsync(executablePath, """
			#!/bin/sh
			echo "claude 9.9.9" >&2
			exit 0
			""");
		MakeExecutable(executablePath);

		var searchPath = string.Join(Path.PathSeparator, new[]
		{
			_binDirectory,
			Environment.GetEnvironmentVariable("PATH") ?? string.Empty
		});

		var result = await _service.DetectAsync(
			ProviderType.Claude,
			"claude",
			"--version",
			searchPath: searchPath);

		Assert.True(result.IsInstalled);
		Assert.Equal("claude 9.9.9", result.Version);
		Assert.Equal(executablePath, result.ResolvedExecutablePath);
	}

	public void Dispose()
	{
		if (Directory.Exists(_tempDirectory))
		{
			Directory.Delete(_tempDirectory, recursive: true);
		}
	}

	private static void MakeExecutable(string filePath)
	{
		if (!OperatingSystem.IsWindows())
		{
			File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		}
	}
}
