using System.Text;
using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Validation;

namespace VibeSwarm.Web.Services;

public interface IProjectMemoryService
{
	Task<string?> PrepareMemoryFileAsync(Project? project, CancellationToken cancellationToken = default);
	Task SyncMemoryFromFileAsync(Guid projectId, string? memoryFilePath, CancellationToken cancellationToken = default);
}

public sealed class ProjectMemoryService(
	VibeSwarmDbContext dbContext,
	ILogger<ProjectMemoryService> logger) : IProjectMemoryService
{
	private const string MemoryDirectoryName = ".vibeswarm";
	private const string MemoryFileName = "project-memory.md";
	private const string GitExcludePattern = ".vibeswarm/";
	private static readonly UTF8Encoding Utf8NoBom = new(false);

	public async Task<string?> PrepareMemoryFileAsync(Project? project, CancellationToken cancellationToken = default)
	{
		if (project == null || string.IsNullOrWhiteSpace(project.WorkingPath))
		{
			return null;
		}

		var workingPath = project.WorkingPath.Trim();
		if (!Directory.Exists(workingPath))
		{
			logger.LogDebug("Skipping project memory file preparation because working path {WorkingPath} does not exist.", workingPath);
			return null;
		}

		var memoryDirectory = Path.Combine(workingPath, MemoryDirectoryName);
		Directory.CreateDirectory(memoryDirectory);
		await EnsureGitExcludeEntryAsync(workingPath, cancellationToken);

		var memoryFilePath = Path.Combine(memoryDirectory, MemoryFileName);
		var normalizedMemory = NormalizeMemory(project.Memory) ?? string.Empty;
		await File.WriteAllTextAsync(memoryFilePath, normalizedMemory, Utf8NoBom, cancellationToken);

		return memoryFilePath;
	}

	public async Task SyncMemoryFromFileAsync(Guid projectId, string? memoryFilePath, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(memoryFilePath) || !File.Exists(memoryFilePath))
		{
			return;
		}

		var updatedMemory = NormalizeMemory(await File.ReadAllTextAsync(memoryFilePath, cancellationToken));
		if (updatedMemory != null && updatedMemory.Length > ValidationLimits.ProjectMemoryMaxLength)
		{
			throw new InvalidOperationException($"Project memory exceeds the {ValidationLimits.ProjectMemoryMaxLength} character limit.");
		}

		var project = await dbContext.Projects.FirstOrDefaultAsync(project => project.Id == projectId, cancellationToken);
		if (project == null)
		{
			logger.LogWarning("Skipping project memory sync because project {ProjectId} no longer exists.", projectId);
			return;
		}

		var currentMemory = NormalizeMemory(project.Memory);
		if (string.Equals(currentMemory, updatedMemory, StringComparison.Ordinal))
		{
			return;
		}

		project.Memory = updatedMemory;
		project.UpdatedAt = DateTime.UtcNow;
		await dbContext.SaveChangesAsync(cancellationToken);
	}

	private static string? NormalizeMemory(string? memory)
	{
		if (string.IsNullOrWhiteSpace(memory))
		{
			return null;
		}

		return memory
			.Replace("\r\n", "\n", StringComparison.Ordinal)
			.Replace("\r", "\n", StringComparison.Ordinal)
			.Trim();
	}

	private static async Task EnsureGitExcludeEntryAsync(string workingPath, CancellationToken cancellationToken)
	{
		var gitInfoDirectory = Path.Combine(workingPath, ".git", "info");
		if (!Directory.Exists(gitInfoDirectory))
		{
			return;
		}

		var excludeFilePath = Path.Combine(gitInfoDirectory, "exclude");
		if (!File.Exists(excludeFilePath))
		{
			return;
		}

		var existingContent = await File.ReadAllTextAsync(excludeFilePath, cancellationToken);
		var lines = existingContent
			.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
		if (lines.Contains(GitExcludePattern, StringComparer.Ordinal))
		{
			return;
		}

		var builder = new StringBuilder(existingContent);
		if (builder.Length > 0 && !existingContent.EndsWith('\n'))
		{
			builder.AppendLine();
		}

		builder.AppendLine(GitExcludePattern);
		await File.WriteAllTextAsync(excludeFilePath, builder.ToString(), Utf8NoBom, cancellationToken);
	}
}
