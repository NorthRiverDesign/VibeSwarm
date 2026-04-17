using System.Formats.Tar;
using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

public sealed class MarketplaceSkillInstallerTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly DbContextOptions<VibeSwarmDbContext> _dbOptions;
	private readonly string _skillStorageRoot;
	private readonly string? _previousSkillsPathOverride;

	public MarketplaceSkillInstallerTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();

		_dbOptions = new DbContextOptionsBuilder<VibeSwarmDbContext>()
			.UseSqlite(_connection)
			.Options;

		using var dbContext = new VibeSwarmDbContext(_dbOptions);
		dbContext.Database.EnsureCreated();

		_skillStorageRoot = Path.Combine(Path.GetTempPath(), "vibeswarm-tests", "market-" + Guid.NewGuid().ToString("N"));
		_previousSkillsPathOverride = Environment.GetEnvironmentVariable("VIBESWARM_SKILLS_PATH");
		Environment.SetEnvironmentVariable("VIBESWARM_SKILLS_PATH", _skillStorageRoot);
	}

	[Fact]
	public async Task StageAsync_ExtractsTargetFolderFromTarball_AndPopulatesMetadata()
	{
		var fakeCatalog = new FakeCatalogClient
		{
			Catalog =
			[
				new MarketplaceSkillSummary
				{
					Slug = "pdf",
					Name = "PDF Skill",
					Description = "Extract text from PDFs.",
					Ref = "abc1234",
				}
			],
			TarballFactory = () => CreateFakeTarball(prefix: "anthropics-skills-abc1234")
		};
		var installer = new MarketplaceSkillInstaller(fakeCatalog);

		var (preview, stagedDirectory) = await installer.StageAsync(new SkillInstallRequest
		{
			Source = SkillInstallSource.Marketplace,
			MarketplaceSlug = "pdf",
		});

		try
		{
			Assert.Equal("pdf", preview.Name);
			Assert.Equal(SkillSourceType.Marketplace, preview.SourceType);
			Assert.Equal("anthropics/skills/pdf", preview.SourceUri);
			Assert.Equal("abc1234", preview.SourceRef);
			Assert.Contains("Extract text", preview.Content);
			Assert.True(File.Exists(Path.Combine(stagedDirectory, "SKILL.md")));
			Assert.True(File.Exists(Path.Combine(stagedDirectory, "references", "guide.md")));
			// Other skills in the tarball must not leak into the staged folder.
			Assert.False(File.Exists(Path.Combine(stagedDirectory, "other-skill-root-file.md")));
		}
		finally
		{
			if (Directory.Exists(stagedDirectory))
			{
				Directory.Delete(stagedDirectory, recursive: true);
			}
		}
	}

	[Fact]
	public async Task StageAsync_ThrowsWhenSlugIsNotInCatalog()
	{
		var catalog = new FakeCatalogClient
		{
			Catalog = [], // empty
			TarballFactory = () => throw new InvalidOperationException("should not be called")
		};
		var installer = new MarketplaceSkillInstaller(catalog);

		await Assert.ThrowsAsync<InvalidOperationException>(() => installer.StageAsync(new SkillInstallRequest
		{
			Source = SkillInstallSource.Marketplace,
			MarketplaceSlug = "missing",
		}));
	}

	[Fact]
	public async Task StageAsync_ThrowsWhenTarballDoesNotContainTheSlugFolder()
	{
		var catalog = new FakeCatalogClient
		{
			Catalog = [new MarketplaceSkillSummary { Slug = "only-in-catalog", Name = "n", Ref = "HEAD" }],
			TarballFactory = () => CreateFakeTarball(prefix: "anthropics-skills-xyz")
		};
		var installer = new MarketplaceSkillInstaller(catalog);

		await Assert.ThrowsAsync<InvalidDataException>(() => installer.StageAsync(new SkillInstallRequest
		{
			Source = SkillInstallSource.Marketplace,
			MarketplaceSlug = "only-in-catalog",
		}));
	}

	[Fact]
	public async Task InstallerService_UsesMarketplaceStrategy_AndWritesInstalledSkill()
	{
		await using var dbContext = new VibeSwarmDbContext(_dbOptions);
		var storage = new SkillStorageService(dbContext, NullLogger<SkillStorageService>.Instance);
		var catalog = new FakeCatalogClient
		{
			Catalog =
			[
				new MarketplaceSkillSummary
				{
					Slug = "pdf",
					Name = "PDF Skill",
					Description = "Extract.",
					AllowedTools = "Bash(python:*)",
					Ref = "deadbeef",
				}
			],
			TarballFactory = () => CreateFakeTarball(prefix: "anthropics-skills-deadbeef")
		};
		var installer = new SkillInstallerService(
			dbContext,
			storage,
			[new MarketplaceSkillInstaller(catalog)],
			NullLogger<SkillInstallerService>.Instance);

		var result = await installer.InstallAsync(new SkillInstallRequest
		{
			Source = SkillInstallSource.Marketplace,
			MarketplaceSlug = "pdf",
		});

		Assert.True(result.Installed);
		Assert.NotNull(result.Skill);
		Assert.Equal(SkillSourceType.Marketplace, result.Skill!.SourceType);
		Assert.Equal("anthropics/skills/pdf", result.Skill.SourceUri);
		Assert.Equal("deadbeef", result.Skill.SourceRef);
		Assert.NotNull(result.Skill.StoragePath);
		Assert.True(File.Exists(Path.Combine(result.Skill.StoragePath!, "SKILL.md")));
		Assert.True(File.Exists(Path.Combine(result.Skill.StoragePath!, "references", "guide.md")));
	}

	/// <summary>
	/// Builds an in-memory gzipped tarball that mimics GitHub's tarball layout:
	/// a single top-level folder (<paramref name="prefix"/>) containing the `pdf/` skill folder
	/// with a SKILL.md + references/guide.md, plus an unrelated sibling entry that must NOT
	/// be extracted by the installer.
	/// </summary>
	private static Stream CreateFakeTarball(string prefix)
	{
		var output = new MemoryStream();
		using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
		using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
		{
			// Sibling file to prove the installer filters to the target subfolder only.
			AddFile(tar, $"{prefix}/other-skill-root-file.md", "Should not be extracted.");
			AddFile(tar, $"{prefix}/pdf/SKILL.md",
				"""
				---
				name: pdf
				description: Extract text from PDFs.
				allowed-tools: Bash(python:*) Bash(pip:*)
				---

				# PDF Skill

				Extract text from PDFs.
				""");
			AddFile(tar, $"{prefix}/pdf/references/guide.md", "# Reference\nExtra guidance.");
		}

		output.Position = 0;
		return output;
	}

	private static void AddFile(TarWriter writer, string path, string content)
	{
		var bytes = System.Text.Encoding.UTF8.GetBytes(content);
		var entry = new PaxTarEntry(TarEntryType.RegularFile, path)
		{
			DataStream = new MemoryStream(bytes),
			Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
		};
		writer.WriteEntry(entry);
	}

	public void Dispose()
	{
		Environment.SetEnvironmentVariable("VIBESWARM_SKILLS_PATH", _previousSkillsPathOverride);
		_connection.Dispose();
		try
		{
			if (Directory.Exists(_skillStorageRoot))
			{
				Directory.Delete(_skillStorageRoot, recursive: true);
			}
		}
		catch
		{
			// best-effort
		}
	}

	private sealed class FakeCatalogClient : IGitHubSkillCatalogClient
	{
		public IReadOnlyList<MarketplaceSkillSummary> Catalog { get; init; } = [];
		public Func<Stream> TarballFactory { get; init; } = () => throw new NotSupportedException();

		public Task<IReadOnlyList<MarketplaceSkillSummary>> ListSkillsAsync(CancellationToken cancellationToken = default)
			=> Task.FromResult(Catalog);

		public Task<Stream> DownloadTarballAsync(string gitRef, CancellationToken cancellationToken = default)
			=> Task.FromResult(TarballFactory());

		public void InvalidateCache() { }
	}
}
