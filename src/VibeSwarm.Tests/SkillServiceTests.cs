using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.Validation;

namespace VibeSwarm.Tests;

public sealed class SkillServiceTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly DbContextOptions<VibeSwarmDbContext> _dbOptions;
	private readonly string _skillStorageRoot;
	private readonly string? _previousSkillsPathOverride;

	public SkillServiceTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();

		_dbOptions = new DbContextOptionsBuilder<VibeSwarmDbContext>()
			.UseSqlite(_connection)
			.Options;

		using var dbContext = CreateDbContext();
		dbContext.Database.EnsureCreated();

		// Isolate skill storage under a throwaway temp directory so installs don't touch
		// the developer's real ~/.local/share tree.
		_skillStorageRoot = Path.Combine(Path.GetTempPath(), "vibeswarm-tests", "skills-" + Guid.NewGuid().ToString("N"));
		_previousSkillsPathOverride = Environment.GetEnvironmentVariable("VIBESWARM_SKILLS_PATH");
		Environment.SetEnvironmentVariable("VIBESWARM_SKILLS_PATH", _skillStorageRoot);
	}

	[Fact]
	public async Task PreviewImportAsync_ExtractsSkillMetadataAndReferenceContent()
	{
		await using var dbContext = CreateDbContext();
		var service = CreateService(dbContext);
		var request = CreateImportRequest(
			"bootstrap-ui.skill",
			"""
			---
			name: bootstrap-ui
			description: >
			  A polished Bootstrap UI skill that should be visible to every provider.
			---

			# Bootstrap UI

			Build polished Bootstrap layouts.
			""",
			new Dictionary<string, string>
			{
				["bootstrap-ui/references/components.md"] = "# Components\n\nUse cards and buttons."
			});

		var preview = await service.PreviewImportAsync(request);

		Assert.Equal("bootstrap-ui", preview.Name);
		Assert.Equal("A polished Bootstrap UI skill that should be visible to every provider.", preview.Description);
		Assert.Contains("# Bootstrap UI", preview.Content);
		Assert.Contains("## Reference: components", preview.Content);
		Assert.Contains("Use cards and buttons.", preview.Content);
		Assert.False(preview.NameExists);
		// The installer refactor strips the archive's root folder from file paths so they
		// read like the post-install layout. Older assertions expected the leading
		// "bootstrap-ui/" directory which was incidental to the Claude export format.
		Assert.Contains("SKILL.md", preview.IncludedFiles);
		Assert.Contains("references/components.md", preview.IncludedFiles);
	}

	[Fact]
	public async Task ImportAsync_CreatesSkillFromArchive()
	{
		await using var dbContext = CreateDbContext();
		var service = CreateService(dbContext);
		var request = CreateImportRequest(
			"bootstrap-ui.skill",
			"""
			---
			name: bootstrap-ui
			description: >
			  A polished Bootstrap UI skill.
			---

			# Bootstrap UI

			Build polished Bootstrap layouts.
			""");

		var result = await service.ImportAsync(request);

		Assert.True(result.Imported);
		Assert.False(result.Skipped);
		Assert.Equal("Installed skill 'bootstrap-ui'.", result.Message);

		var saved = await dbContext.Skills.SingleAsync();
		Assert.Equal("bootstrap-ui", saved.Name);
		Assert.Equal("A polished Bootstrap UI skill.", saved.Description);
		Assert.Contains("# Bootstrap UI", saved.Content);
		Assert.True(saved.IsEnabled);
	}

	[Fact]
	public async Task ImportAsync_PopulatesInstallMetadataAndMaterializesFolder()
	{
		await using var dbContext = CreateDbContext();
		var service = CreateService(dbContext);
		var request = CreateImportRequest(
			"pdf.skill",
			"""
			---
			name: pdf
			description: Extract text and fill PDF forms.
			allowed-tools: Bash(python:*) Bash(pip:*)
			---

			# PDF Skill

			Instructions for working with PDFs.
			""");

		var result = await service.ImportAsync(request);
		Assert.True(result.Imported);

		var saved = await dbContext.Skills.SingleAsync();
		Assert.Equal(SkillSourceType.ZipImport, saved.SourceType);
		Assert.Equal("pdf.skill", saved.SourceUri);
		Assert.Null(saved.SourceRef);
		Assert.False(saved.HasScripts);
		Assert.Equal("Bash(python:*) Bash(pip:*)", saved.AllowedTools);
		Assert.NotNull(saved.InstalledAt);
		Assert.NotNull(saved.StoragePath);

		// The storage service should have written SKILL.md into the central storage directory
		// so PromptBuilder can reference it by absolute path.
		Assert.True(Directory.Exists(saved.StoragePath));
		var manifestPath = Path.Combine(saved.StoragePath!, "SKILL.md");
		Assert.True(File.Exists(manifestPath));
		Assert.Contains("# PDF Skill", await File.ReadAllTextAsync(manifestPath));
	}

	[Fact]
	public async Task ImportAsync_SkipsWhenSkillAlreadyExists()
	{
		await using var dbContext = CreateDbContext();
		dbContext.Skills.Add(new Skill
		{
			Id = Guid.NewGuid(),
			Name = "bootstrap-ui",
			Description = "Existing skill",
			Content = "Existing content"
		});
		await dbContext.SaveChangesAsync();

		var service = CreateService(dbContext);
		var request = CreateImportRequest(
			"bootstrap-ui.skill",
			"""
			---
			name: bootstrap-ui
			description: >
			  Imported description.
			---

			# Bootstrap UI
			""");

		var result = await service.ImportAsync(request);

		Assert.False(result.Imported);
		Assert.True(result.Skipped);
		Assert.Equal("Skill 'bootstrap-ui' already exists.", result.Message);
		Assert.Single(await dbContext.Skills.ToListAsync());
	}

	[Fact]
	public async Task PreviewImportAsync_TruncatesLongDescriptionWithWarning()
	{
		await using var dbContext = CreateDbContext();
		var service = CreateService(dbContext);
		var longDescription = new string('a', ValidationLimits.SkillDescriptionMaxLength + 20);
		var request = CreateImportRequest(
			"long-description.skill",
			$"""
			---
			name: long-description
			description: >
			  {longDescription}
			---

			# Skill
			""");

		var preview = await service.PreviewImportAsync(request);

		Assert.Equal(ValidationLimits.SkillDescriptionMaxLength, preview.Description!.Length);
		Assert.Contains(
			preview.Warnings,
			warning => warning == $"Description was truncated to {ValidationLimits.SkillDescriptionMaxLength} characters.");
	}

	private VibeSwarmDbContext CreateDbContext() => new(_dbOptions);

	private static SkillService CreateService(VibeSwarmDbContext dbContext)
	{
		var storage = new SkillStorageService(dbContext, NullLogger<SkillStorageService>.Instance);
		var installer = new SkillInstallerService(
			dbContext,
			storage,
			[new ZipSkillInstaller()],
			NullLogger<SkillInstallerService>.Instance);
		return new SkillService(dbContext, new NoOpProviderService(), installer, NullLogger<SkillService>.Instance);
	}

	private static SkillImportRequest CreateImportRequest(
		string fileName,
		string skillMarkdown,
		Dictionary<string, string>? additionalFiles = null)
	{
		using var stream = new MemoryStream();
		using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
		{
			var rootName = Path.GetFileNameWithoutExtension(fileName);
			var skillEntry = archive.CreateEntry($"{rootName}/SKILL.md");
			using (var writer = new StreamWriter(skillEntry.Open(), Encoding.UTF8, leaveOpen: false))
			{
				writer.Write(skillMarkdown);
			}

			if (additionalFiles != null)
			{
				foreach (var (path, content) in additionalFiles)
				{
					var entry = archive.CreateEntry(path);
					using var writer = new StreamWriter(entry.Open(), Encoding.UTF8, leaveOpen: false);
					writer.Write(content);
				}
			}
		}

		return new SkillImportRequest
		{
			FileName = fileName,
			Content = stream.ToArray()
		};
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
			// best-effort cleanup
		}
	}

	private sealed class NoOpProviderService : IProviderService
	{
		public Task<IEnumerable<Provider>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Provider>>([]);
		public Task<Provider?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Provider?>(null);
		public Task<Provider?> GetDefaultAsync(CancellationToken cancellationToken = default) => Task.FromResult<Provider?>(null);
		public IProvider? CreateInstance(Provider config) => null;
		public Task<Provider> CreateAsync(Provider provider, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Provider> UpdateAsync(Provider provider, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> TestConnectionAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<ConnectionTestResult> TestConnectionWithDetailsAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task SetEnabledAsync(Guid id, bool isEnabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task SetDefaultAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<SessionSummary> GetSessionSummaryAsync(Guid providerId, string? sessionId, string? workingDirectory = null, string? fallbackOutput = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IEnumerable<ProviderModel>> GetModelsAsync(Guid providerId, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<ProviderModel>>([]);
		public Task<IEnumerable<ProviderModel>> RefreshModelsAsync(Guid providerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task SetDefaultModelAsync(Guid providerId, Guid modelId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<CliUpdateResult> UpdateCliAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}
}
