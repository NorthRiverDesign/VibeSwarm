using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Services;

public interface ILocalEnvironmentSetupService
{
	/// <summary>
	/// Saves what a finished local setup run reported as the project's Local environment, then
	/// deletes the result file. Returns the environment, or null when there was nothing usable.
	/// </summary>
	Task<ProjectEnvironment?> ApplyResultAsync(Guid projectId, string? workingDirectory, CancellationToken cancellationToken = default);
}

public sealed class LocalEnvironmentSetupService(
	VibeSwarmDbContext dbContext,
	IProjectEnvironmentCredentialService credentialService,
	ILogger<LocalEnvironmentSetupService> logger) : ILocalEnvironmentSetupService
{
	private const string ResultDirectoryName = ".vibeswarm";

	public static string GetResultFilePath(string workingDirectory) =>
		Path.Combine(workingDirectory, ResultDirectoryName, LocalEnvironmentSetup.ResultFileName);

	/// <summary>
	/// Clears any result left by an earlier run, so a run that reports nothing can't re-apply an
	/// old one, and returns the path the agent should write to.
	/// </summary>
	public static string PrepareResultFile(string workingDirectory)
	{
		var resultFilePath = GetResultFilePath(workingDirectory);
		Directory.CreateDirectory(Path.GetDirectoryName(resultFilePath)!);
		DeleteResultFile(resultFilePath);
		return resultFilePath;
	}

	public async Task<ProjectEnvironment?> ApplyResultAsync(Guid projectId, string? workingDirectory, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(workingDirectory))
		{
			return null;
		}

		var resultFilePath = GetResultFilePath(workingDirectory);
		if (!File.Exists(resultFilePath))
		{
			logger.LogInformation("Local setup for project {ProjectId} finished without a result file", projectId);
			return null;
		}

		string json;
		try
		{
			json = await File.ReadAllTextAsync(resultFilePath, cancellationToken);
		}
		finally
		{
			// It can hold a local login, so it never outlives the run.
			DeleteResultFile(resultFilePath);
		}

		var result = LocalEnvironmentSetup.ParseResult(json);
		if (result == null)
		{
			logger.LogWarning("Local setup for project {ProjectId} wrote a result file that is not valid JSON", projectId);
			return null;
		}

		var project = await dbContext.Projects
			.Include(item => item.Environments)
			.FirstOrDefaultAsync(item => item.Id == projectId, cancellationToken);
		if (project == null)
		{
			return null;
		}

		var existingIds = project.Environments.Select(item => item.Id).ToHashSet();
		var environment = LocalEnvironmentSetup.ApplyResult(project, result);
		if (environment == null)
		{
			logger.LogWarning("Local setup for project {ProjectId} reported no usable URL", projectId);
			return null;
		}

		if (!existingIds.Contains(environment.Id))
		{
			dbContext.ProjectEnvironments.Add(environment);
		}

		if (environment.Username != null || environment.Password != null)
		{
			// Only this environment's login changed; the saved password stays unless a new one came back.
			credentialService.PrepareForStorage(new Project { Environments = [environment] }, [environment]);
		}

		project.UpdatedAt = DateTime.UtcNow;
		await dbContext.SaveChangesAsync(cancellationToken);
		return environment;
	}

	private static void DeleteResultFile(string resultFilePath)
	{
		if (File.Exists(resultFilePath))
		{
			File.Delete(resultFilePath);
		}
	}
}
