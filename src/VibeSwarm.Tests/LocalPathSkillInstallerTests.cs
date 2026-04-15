using System.ComponentModel.DataAnnotations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

public sealed class LocalPathSkillInstallerTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly DbContextOptions<VibeSwarmDbContext> _dbOptions;
	private readonly string _skillStorageRoot;
	private readonly string? _previousSkillsPathOverride;
	private readonly List<string> _cleanupPaths = [];

	public LocalPathSkillInstallerTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();

		_dbOptions = new DbContextOptionsBuilder<VibeSwarmDbContext>()
			.UseSqlite(_connection)
			.Options;

		using var dbContext = new VibeSwarmDbContext(_dbOptions);
		dbContext.Database.EnsureCreated();

		_skillStorageRoot = Path.Combine(Path.GetTempPath(), "vibeswarm-tests", "local-" + Guid.NewGuid().ToString("N"));
		_previousSkillsPathOverride = Environment.GetEnvironmentVariable("VIBESWARM_SKILLS_PATH");
		Environment.SetEnvironmentVariable("VIBESWARM_SKILLS_PATH", _skillStorageRoot);
	}

	[Fact]
	public async Task StageAsync_CopiesFolderAndReadsMetadata()
	{
		var source = CreateSkillFolder(
			name: "pdf",
			manifest:
				"""
				---
				name: pdf
				description: Extract PDF text.
				allowed-tools: Bash(python:*)
				---

				# PDF
				""",
			referenceFiles: new Dictionary<string, string>
			{
				["references/guide.md"] = "# Reference",
				["scripts/run.sh"] = "#!/bin/sh\necho hi",
			});

		var installer = new LocalPathSkillInstaller();
		var (preview, staged) = await installer.StageAsync(new SkillInstallRequest
		{
			Source = SkillInstallSource.LocalPath,
			LocalPath = source,
		});
		_cleanupPaths.Add(staged);

		Assert.Equal("pdf", preview.Name);
		Assert.Equal(SkillSourceType.LocalPath, preview.SourceType);
		Assert.Equal(Path.GetFullPath(source), preview.SourceUri);
		Assert.Null(preview.SourceRef);
		Assert.Equal("Bash(python:*)", preview.AllowedTools);
		Assert.True(preview.HasScripts);
		Assert.True(File.Exists(Path.Combine(staged, "SKILL.md")));
		Assert.True(File.Exists(Path.Combine(staged, "references", "guide.md")));
		Assert.True(File.Exists(Path.Combine(staged, "scripts", "run.sh")));
		Assert.Contains(preview.Files, file => file.RelativePath == "scripts/run.sh");
		Assert.Contains(preview.Warnings, warning => warning.Contains("executable file", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public async Task StageAsync_FallsBackToFolderNameWhenManifestOmitsName()
	{
		var source = CreateSkillFolder(
			name: "my-local-skill",
			manifest: "# No frontmatter\n\nBody only.");

		var installer = new LocalPathSkillInstaller();
		var (preview, staged) = await installer.StageAsync(new SkillInstallRequest
		{
			Source = SkillInstallSource.LocalPath,
			LocalPath = source,
		});
		_cleanupPaths.Add(staged);

		Assert.Equal("my-local-skill", preview.Name);
		// Missing allowed-tools → a warning must be surfaced so the preview modal asks for ack.
		Assert.Contains(preview.Warnings, warning => warning.Contains("allowed-tools", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public async Task StageAsync_ThrowsWhenFolderMissing()
	{
		var installer = new LocalPathSkillInstaller();
		var missingPath = Path.Combine(Path.GetTempPath(), "vibeswarm-does-not-exist-" + Guid.NewGuid().ToString("N"));

		await Assert.ThrowsAsync<ValidationException>(() => installer.StageAsync(new SkillInstallRequest
		{
			Source = SkillInstallSource.LocalPath,
			LocalPath = missingPath,
		}));
	}

	[Fact]
	public async Task StageAsync_ThrowsWhenFolderHasNoSkillManifest()
	{
		var source = Path.Combine(Path.GetTempPath(), "vibeswarm-tests", "no-manifest-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(source);
		_cleanupPaths.Add(source);
		await File.WriteAllTextAsync(Path.Combine(source, "README.md"), "Just a readme.");

		var installer = new LocalPathSkillInstaller();
		await Assert.ThrowsAsync<ValidationException>(() => installer.StageAsync(new SkillInstallRequest
		{
			Source = SkillInstallSource.LocalPath,
			LocalPath = source,
		}));
	}

	[Fact]
	public async Task InstallAsync_ThroughOrchestrator_WritesLocalPathSourceMetadata()
	{
		var source = CreateSkillFolder(
			name: "local-install-test",
			manifest:
				"""
				---
				name: local-install-test
				description: Local install smoke.
				allowed-tools: Read
				---

				# Local install test
				""");

		await using var dbContext = new VibeSwarmDbContext(_dbOptions);
		var storage = new SkillStorageService(dbContext, NullLogger<SkillStorageService>.Instance);
		var installer = new SkillInstallerService(
			dbContext,
			storage,
			[new LocalPathSkillInstaller()],
			NullLogger<SkillInstallerService>.Instance);

		var result = await installer.InstallAsync(new SkillInstallRequest
		{
			Source = SkillInstallSource.LocalPath,
			LocalPath = source,
		});

		Assert.True(result.Installed);
		Assert.NotNull(result.Skill);
		Assert.Equal(SkillSourceType.LocalPath, result.Skill!.SourceType);
		Assert.Equal(Path.GetFullPath(source), result.Skill.SourceUri);
		Assert.Null(result.Skill.SourceRef);
		Assert.Equal("Read", result.Skill.AllowedTools);
		Assert.NotNull(result.Skill.StoragePath);
		Assert.True(File.Exists(Path.Combine(result.Skill.StoragePath!, "SKILL.md")));
	}

	private string CreateSkillFolder(string name, string manifest, IDictionary<string, string>? referenceFiles = null)
	{
		var root = Path.Combine(Path.GetTempPath(), "vibeswarm-tests", "local-src-" + Guid.NewGuid().ToString("N"), name);
		Directory.CreateDirectory(root);
		_cleanupPaths.Add(Path.GetDirectoryName(root)!);

		File.WriteAllText(Path.Combine(root, "SKILL.md"), manifest);

		if (referenceFiles is not null)
		{
			foreach (var (relative, content) in referenceFiles)
			{
				var path = Path.Combine(root, relative);
				Directory.CreateDirectory(Path.GetDirectoryName(path)!);
				File.WriteAllText(path, content);

				if (relative.StartsWith("scripts/", StringComparison.OrdinalIgnoreCase) && !OperatingSystem.IsWindows())
				{
					try
					{
						var mode = File.GetUnixFileMode(path) | UnixFileMode.UserExecute;
						File.SetUnixFileMode(path, mode);
					}
					catch (IOException)
					{
						// test best-effort
					}
				}
			}
		}

		return root;
	}

	public void Dispose()
	{
		Environment.SetEnvironmentVariable("VIBESWARM_SKILLS_PATH", _previousSkillsPathOverride);
		_connection.Dispose();

		foreach (var path in _cleanupPaths.Concat([_skillStorageRoot]))
		{
			try
			{
				if (Directory.Exists(path))
				{
					Directory.Delete(path, recursive: true);
				}
			}
			catch
			{
				// best-effort
			}
		}
	}
}
