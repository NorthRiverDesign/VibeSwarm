using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.VersionControl;

namespace VibeSwarm.Web.Services;

/// <summary>How much of a project's history <see cref="ProjectContextBuilder"/> includes.</summary>
public sealed record ProjectContextOptions
{
	public static readonly ProjectContextOptions FolderReport = new();

	public int MaxRecentJobs { get; init; } = 10;

	public int MaxSectionLength { get; init; } = 6000;
}

/// <summary>
/// Describes where a project stands, as plain text for a model: its queue, running and recent
/// jobs with their outcomes, git status and recent commits. Shared by the folder manager and the
/// project Lead.
/// </summary>
public sealed partial class ProjectContextBuilder
{
	internal const int MaxIdeasPerProject = 15;
	internal const int MaxActiveJobsPerProject = 5;
	internal const int MaxCommitsPerProject = 12;
	internal const int MaxTodoFileLength = 3000;
	private const int MaxIdeaLength = 300;
	private const int MaxJobLineLength = 400;

	internal static readonly string[] TodoFileNames = ["TODO.md", "todo.md", "TODO", "TODO.txt", "todo.txt"];

	private static readonly JobStatus[] ActiveStatuses =
		[JobStatus.New, JobStatus.Pending, JobStatus.Started, JobStatus.Planning, JobStatus.Processing, JobStatus.Paused];

	private static readonly JobStatus[] FinishedStatuses =
		[JobStatus.Completed, JobStatus.Failed, JobStatus.Cancelled, JobStatus.Stalled];

	private readonly VibeSwarmDbContext _db;
	private readonly IGitCommandExecutor _git;
	private readonly ILogger _logger;

	public ProjectContextBuilder(VibeSwarmDbContext db, IGitCommandExecutor git, ILogger logger)
	{
		_db = db;
		_git = git;
		_logger = logger;
	}

	public async Task<string> BuildProjectSectionAsync(Project project, ProjectContextOptions options, CancellationToken cancellationToken)
	{
		var builder = new StringBuilder();
		builder.Append("## Project: ").AppendLine(project.Name);
		if (!project.IsActive)
		{
			builder.AppendLine("Marked inactive by the owner.");
		}
		if (!string.IsNullOrWhiteSpace(project.Description))
		{
			builder.Append("Description: ").AppendLine(Truncate(OneLine(project.Description), 500));
		}
		if (!string.IsNullOrWhiteSpace(project.GitHubRepository))
		{
			builder.Append("Repository: ").AppendLine(project.GitHubRepository);
		}

		var hasWorkingPath = !string.IsNullOrWhiteSpace(project.WorkingPath) && Directory.Exists(project.WorkingPath);
		if (hasWorkingPath)
		{
			await AppendGitStatusAsync(builder, project.WorkingPath, cancellationToken);
		}

		var ideaQuery = _db.Ideas.AsNoTracking().Where(i => i.ProjectId == project.Id && !i.IsProcessing && i.JobId == null);
		var ideaCount = await ideaQuery.CountAsync(cancellationToken);
		builder.AppendLine();
		if (ideaCount == 0)
		{
			builder.AppendLine("Queued ideas (to-do): none.");
		}
		else
		{
			var ideas = await ideaQuery
				.OrderBy(i => i.SortOrder)
				.ThenBy(i => i.CreatedAt)
				.Take(MaxIdeasPerProject)
				.Select(i => i.Description)
				.ToListAsync(cancellationToken);
			builder.Append("Queued ideas (to-do), ").Append(ideaCount).AppendLine(ideaCount > ideas.Count ? $" in all, first {ideas.Count}:" : ":");
			foreach (var idea in ideas)
			{
				builder.Append("- ").AppendLine(Truncate(OneLine(idea), MaxIdeaLength));
			}
		}

		if (hasWorkingPath)
		{
			var todo = ReadTodoFile(project.WorkingPath);
			if (todo != null)
			{
				builder.AppendLine();
				builder.Append("To-do file ").Append(todo.Value.Name).AppendLine(" in the repository:");
				builder.AppendLine(todo.Value.Content);
			}
		}

		var activeJobs = await _db.Jobs
			.AsNoTracking()
			.Where(j => j.ProjectId == project.Id && ActiveStatuses.Contains(j.Status))
			.OrderBy(j => j.CreatedAt)
			.Take(MaxActiveJobsPerProject)
			.Select(j => new { j.Title, j.GoalPrompt, j.Status, j.CurrentActivity })
			.ToListAsync(cancellationToken);
		builder.AppendLine();
		if (activeJobs.Count == 0)
		{
			builder.AppendLine("Running or waiting jobs: none.");
		}
		else
		{
			builder.AppendLine("Running or waiting jobs:");
			foreach (var job in activeJobs)
			{
				var line = $"[{job.Status}] {JobLabel(job.Title, job.GoalPrompt)}";
				if (!string.IsNullOrWhiteSpace(job.CurrentActivity))
				{
					line += $" (now: {OneLine(job.CurrentActivity)})";
				}
				builder.Append("- ").AppendLine(Truncate(line, MaxJobLineLength));
			}
		}

		var recentJobs = await _db.Jobs
			.AsNoTracking()
			.Where(j => j.ProjectId == project.Id && FinishedStatuses.Contains(j.Status))
			.OrderByDescending(j => j.CompletedAt ?? j.CreatedAt)
			.Take(options.MaxRecentJobs)
			.Select(j => new { j.Title, j.GoalPrompt, j.Status, j.CompletedAt, j.CreatedAt, j.SessionSummary, j.ErrorMessage, j.GitCommitHash })
			.ToListAsync(cancellationToken);
		builder.AppendLine();
		if (recentJobs.Count == 0)
		{
			builder.AppendLine("Recently finished jobs: none.");
		}
		else
		{
			builder.AppendLine("Recently finished jobs, newest first:");
			foreach (var job in recentJobs)
			{
				var line = $"{FormatDate(job.CompletedAt ?? job.CreatedAt)} [{job.Status}] {JobLabel(job.Title, job.GoalPrompt)}";
				if (job.Status != JobStatus.Completed && !string.IsNullOrWhiteSpace(job.ErrorMessage))
				{
					line += $" | error: {OneLine(job.ErrorMessage)}";
				}
				else if (!string.IsNullOrWhiteSpace(job.SessionSummary))
				{
					line += $" | outcome: {OneLine(job.SessionSummary)}";
				}
				if (job.Status == JobStatus.Completed && string.IsNullOrWhiteSpace(job.GitCommitHash))
				{
					line += " | not committed";
				}
				builder.Append("- ").AppendLine(Truncate(line, MaxJobLineLength));
			}
		}

		if (hasWorkingPath)
		{
			await AppendRecentCommitsAsync(builder, project.WorkingPath, cancellationToken);
		}

		return Truncate(builder.ToString().TrimEnd(), options.MaxSectionLength);
	}

	private async Task AppendGitStatusAsync(StringBuilder builder, string workingPath, CancellationToken cancellationToken)
	{
		var status = await RunGitAsync("status --porcelain --branch", workingPath, cancellationToken);
		if (status == null)
		{
			return;
		}

		var lines = status.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		var branchLine = lines.FirstOrDefault(line => line.StartsWith("##", StringComparison.Ordinal));
		if (branchLine != null)
		{
			builder.Append("Branch: ").AppendLine(branchLine[2..].Trim());
		}

		var changedFiles = lines.Count(line => !line.StartsWith("##", StringComparison.Ordinal));
		builder.AppendLine(changedFiles == 0
			? "Working tree: clean."
			: $"Working tree: {changedFiles} uncommitted file{(changedFiles == 1 ? "" : "s")}.");
	}

	private async Task AppendRecentCommitsAsync(StringBuilder builder, string workingPath, CancellationToken cancellationToken)
	{
		var log = await RunGitAsync(
			$"log -n {MaxCommitsPerProject} --no-decorate --date=short --format=%h%x09%ad%x09%s",
			workingPath,
			cancellationToken);
		if (string.IsNullOrWhiteSpace(log))
		{
			return;
		}

		builder.AppendLine();
		builder.AppendLine("Recent commits, newest first:");
		foreach (var line in log.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var parts = line.Split('\t', 3);
			builder.Append("- ").AppendLine(parts.Length == 3 ? $"{parts[1]} {parts[0]} {parts[2]}" : line);
		}
	}

	private async Task<string?> RunGitAsync(string arguments, string workingPath, CancellationToken cancellationToken)
	{
		try
		{
			var result = await _git.ExecuteAsync(arguments, workingPath, cancellationToken, timeoutSeconds: 15);
			return result.Success ? result.Output : null;
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			_logger.LogDebug(ex, "git {Arguments} failed in {WorkingPath} for project context", arguments, workingPath);
			return null;
		}
	}

	internal static (string Name, string Content)? ReadTodoFile(string workingPath)
	{
		foreach (var name in TodoFileNames)
		{
			var path = Path.Combine(workingPath, name);
			try
			{
				if (!File.Exists(path))
				{
					continue;
				}

				using var reader = new StreamReader(path);
				var buffer = new char[MaxTodoFileLength + 1];
				var read = reader.ReadBlock(buffer, 0, buffer.Length);
				var content = new string(buffer, 0, Math.Min(read, MaxTodoFileLength)).Trim();
				if (content.Length == 0)
				{
					continue;
				}

				return (name, read > MaxTodoFileLength ? content + " …" : content);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				continue;
			}
		}

		return null;
	}

	internal static string JobLabel(string? title, string goalPrompt)
		=> OneLine(string.IsNullOrWhiteSpace(title) ? goalPrompt : title);

	internal static string FormatDate(DateTime value)
		=> value.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

	internal static string OneLine(string text)
		=> WhitespaceRegex().Replace(text, " ").Trim();

	internal static string Truncate(string text, int maxLength)
		=> text.Length > maxLength ? text[..maxLength].TrimEnd() + " …" : text;

	[GeneratedRegex(@"\s+")]
	private static partial Regex WhitespaceRegex();
}
