using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Inference;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.Validation;
using VibeSwarm.Shared.VersionControl;

namespace VibeSwarm.Web.Services;

/// <summary>
/// Runs an inference model as project manager for a folder. Each run reads every project in the
/// folder (queued ideas, a TODO file, active and recent jobs, recent commits and uncommitted work),
/// plus the earlier report it continues from, and saves the report the model writes. Enabled
/// inference providers are tried in priority order, each with its model for
/// <see cref="TaskType"/> or else its default model.
/// </summary>
public sealed partial class FolderManagerService : IFolderManagerService
{
	public const string TaskType = InferenceTaskModels.ProjectManager;

	internal const string SystemPrompt =
		"You are the project manager for a folder of related software projects. Coding agents work on them "
		+ "through VibeSwarm: the owner queues ideas, each idea becomes a job an agent runs, and finished jobs "
		+ "are committed to git. From the facts below, write a status report for the owner, who reads it on a "
		+ "phone. Start with a two or three sentence overview of where the projects stand. Then give each "
		+ "project a short section: what was done recently, what is running or queued, and any problems such "
		+ "as failed or stalled jobs, uncommitted work or a stale queue. End with a short list of next steps "
		+ "in priority order, naming the project for each. Cite job titles, ideas and commit subjects; never "
		+ "invent work. When an earlier report is given, build on it: say what changed since then, which of "
		+ "its next steps are done and which are still open. Write plain text, not Markdown: put each section "
		+ "title on its own line and start list items with \"- \". Keep the whole report under 600 words.";

	internal const int MaxReportsListed = 30;
	internal const int MaxIdeasPerProject = 15;
	internal const int MaxActiveJobsPerProject = 5;
	internal const int MaxRecentJobsPerProject = 10;
	internal const int MaxCommitsPerProject = 12;
	internal const int MaxTodoFileLength = 3000;
	internal const int MaxPreviousReportLength = 8000;
	internal const int MaxProjectSectionLength = 6000;
	private const int MaxIdeaLength = 300;
	private const int MaxJobLineLength = 400;

	internal static readonly string[] TodoFileNames = ["TODO.md", "todo.md", "TODO", "TODO.txt", "todo.txt"];

	private static readonly JobStatus[] ActiveStatuses =
		[JobStatus.New, JobStatus.Pending, JobStatus.Started, JobStatus.Planning, JobStatus.Processing, JobStatus.Paused];

	private static readonly JobStatus[] FinishedStatuses =
		[JobStatus.Completed, JobStatus.Failed, JobStatus.Cancelled, JobStatus.Stalled];

	private readonly VibeSwarmDbContext _db;
	private readonly IInferenceService _inference;
	private readonly IGitCommandExecutor _git;
	private readonly ILogger<FolderManagerService> _logger;

	public FolderManagerService(
		VibeSwarmDbContext db,
		IInferenceService inference,
		IGitCommandExecutor git,
		ILogger<FolderManagerService> logger)
	{
		_db = db;
		_inference = inference;
		_git = git;
		_logger = logger;
	}

	public async Task<IReadOnlyList<FolderManagerReport>> GetReportsAsync(Guid folderId, CancellationToken cancellationToken = default)
	{
		return await _db.FolderManagerReports
			.AsNoTracking()
			.Where(r => r.FolderId == folderId)
			.OrderByDescending(r => r.CreatedAt)
			.Take(MaxReportsListed)
			.ToListAsync(cancellationToken);
	}

	public async Task DeleteReportAsync(Guid reportId, CancellationToken cancellationToken = default)
	{
		var report = await _db.FolderManagerReports.FirstOrDefaultAsync(r => r.Id == reportId, cancellationToken);
		if (report == null)
		{
			return;
		}

		// Later runs that continued from this report keep their own text; they just lose the link.
		var continuations = await _db.FolderManagerReports
			.Where(r => r.PreviousReportId == reportId)
			.ToListAsync(cancellationToken);
		foreach (var continuation in continuations)
		{
			continuation.PreviousReportId = null;
		}

		_db.FolderManagerReports.Remove(report);
		await _db.SaveChangesAsync(cancellationToken);
	}

	public async Task<FolderManagerRunResult> RunAsync(Guid folderId, FolderManagerRunRequest request, CancellationToken cancellationToken = default)
	{
		var folder = await _db.ProjectFolders
			.AsNoTracking()
			.FirstOrDefaultAsync(f => f.Id == folderId, cancellationToken);
		if (folder == null)
		{
			return new FolderManagerRunResult { Error = "Folder not found." };
		}

		var notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
		if (notes?.Length > ValidationLimits.FolderManagerNotesMaxLength)
		{
			return new FolderManagerRunResult
			{
				Error = $"Notes can be at most {ValidationLimits.FolderManagerNotesMaxLength:N0} characters."
			};
		}

		FolderManagerReport? previous = null;
		if (request.ContinueFromReportId is { } previousId)
		{
			previous = await _db.FolderManagerReports
				.AsNoTracking()
				.FirstOrDefaultAsync(r => r.Id == previousId && r.FolderId == folderId, cancellationToken);
			if (previous == null)
			{
				return new FolderManagerRunResult { Error = "The report to continue from no longer exists." };
			}
		}

		var projects = await _db.Projects
			.AsNoTracking()
			.Where(p => p.FolderId == folderId)
			.OrderBy(p => p.Name)
			.ToListAsync(cancellationToken);
		if (projects.Count == 0)
		{
			return new FolderManagerRunResult { Error = "This folder has no projects yet. Choose it in a project's settings first." };
		}

		var sections = new List<string>();
		foreach (var project in projects)
		{
			sections.Add(await BuildProjectSectionAsync(project, cancellationToken));
		}

		var prompt = BuildPrompt(folder.Name, sections, previous, notes, DateTime.UtcNow);
		var reply = await AskAsync(prompt, cancellationToken);
		if (reply == null)
		{
			return new FolderManagerRunResult
			{
				Error = "No inference provider replied. Check that one is enabled and online on the Inference page."
			};
		}

		var content = CleanReport(reply.Response.Response!);
		if (content == null)
		{
			return new FolderManagerRunResult { Error = "The inference model returned an empty report. Try again." };
		}

		var report = new FolderManagerReport
		{
			Id = Guid.NewGuid(),
			FolderId = folderId,
			PreviousReportId = previous?.Id,
			Notes = notes,
			Content = content.Length > ValidationLimits.FolderManagerReportMaxLength
				? content[..ValidationLimits.FolderManagerReportMaxLength]
				: content,
			ProjectNames = Truncate(string.Join(", ", projects.Select(p => p.Name)), 2000),
			ProviderName = reply.ProviderName,
			ProviderType = reply.ProviderType,
			ModelId = reply.ModelId,
			PromptTokens = reply.Response.PromptTokens,
			CompletionTokens = reply.Response.CompletionTokens,
			DurationMs = reply.DurationMs,
			CostUsd = reply.Response.CostUsd,
			CreatedAt = DateTime.UtcNow
		};

		// Saved without the caller's token: the request was spent, so a closed page still keeps it.
		_db.FolderManagerReports.Add(report);
		await _db.SaveChangesAsync(CancellationToken.None);
		return new FolderManagerRunResult { Report = report };
	}

	private sealed record ModelReply(
		string ProviderName,
		InferenceProviderType ProviderType,
		string ModelId,
		InferenceResponse Response,
		long DurationMs);

	private async Task<ModelReply?> AskAsync(string prompt, CancellationToken cancellationToken)
	{
		var providers = await _db.InferenceProviders
			.AsNoTracking()
			.Include(p => p.Models)
			.Where(p => p.IsEnabled)
			.ToListAsync(cancellationToken);

		foreach (var (provider, model) in InferenceTaskModels.Candidates(providers, TaskType))
		{
			InferenceResponse response;
			var startedAt = Stopwatch.GetTimestamp();
			try
			{
				response = await _inference.GenerateAsync(new InferenceRequest
				{
					TaskType = TaskType,
					Prompt = prompt,
					SystemPrompt = SystemPrompt,
					ProviderId = provider.Id,
					Endpoint = provider.Endpoint,
					ProviderType = provider.ProviderType,
					Model = model.ModelId,
					Temperature = 0.3,
					MaxTokens = 4000
				}, cancellationToken);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				_logger.LogWarning(ex, "Inference provider {ProviderName} failed to write a folder report", provider.Name);
				continue;
			}

			if (!response.Success || string.IsNullOrWhiteSpace(response.Response))
			{
				_logger.LogWarning(
					"Inference provider {ProviderName} could not write a folder report: {Error}",
					provider.Name,
					response.Error ?? "No response");
				continue;
			}

			return new ModelReply(
				provider.Name,
				provider.ProviderType,
				response.ModelUsed ?? model.ModelId,
				response,
				response.DurationMs ?? (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
		}

		return null;
	}

	private async Task<string> BuildProjectSectionAsync(Project project, CancellationToken cancellationToken)
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
			.Take(MaxRecentJobsPerProject)
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

		return Truncate(builder.ToString().TrimEnd(), MaxProjectSectionLength);
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
			_logger.LogDebug(ex, "git {Arguments} failed in {WorkingPath} for a folder report", arguments, workingPath);
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

	internal static string BuildPrompt(
		string folderName,
		IReadOnlyList<string> projectSections,
		FolderManagerReport? previous,
		string? notes,
		DateTime nowUtc)
	{
		var builder = new StringBuilder();
		builder.Append("Folder: ").AppendLine(folderName.Trim());
		builder.Append("Today: ").AppendLine(FormatDate(nowUtc));
		builder.Append("Projects: ").Append(projectSections.Count).AppendLine();
		builder.AppendLine();

		foreach (var section in projectSections)
		{
			builder.AppendLine(section);
			builder.AppendLine();
		}

		if (previous != null)
		{
			builder.Append("# Your earlier report, from ").Append(FormatDate(previous.CreatedAt)).AppendLine(":");
			builder.AppendLine(Truncate(previous.Content.Trim(), MaxPreviousReportLength));
			builder.AppendLine();
		}

		if (!string.IsNullOrWhiteSpace(notes))
		{
			builder.AppendLine("# What the owner wants this report to look at:");
			builder.AppendLine(notes.Trim());
		}

		return builder.ToString().Trim();
	}

	/// <summary>The model's report without reasoning blocks, or null when nothing is left.</summary>
	internal static string? CleanReport(string reply)
	{
		var text = ThinkBlockRegex().Replace(reply, string.Empty).Trim();
		return text.Length == 0 ? null : text;
	}

	private static string JobLabel(string? title, string goalPrompt)
		=> OneLine(string.IsNullOrWhiteSpace(title) ? goalPrompt : title);

	private static string FormatDate(DateTime value)
		=> value.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

	private static string OneLine(string text)
		=> WhitespaceRegex().Replace(text, " ").Trim();

	private static string Truncate(string text, int maxLength)
		=> text.Length > maxLength ? text[..maxLength].TrimEnd() + " …" : text;

	[GeneratedRegex(@"<think>.*?(</think>|$)", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
	private static partial Regex ThinkBlockRegex();

	[GeneratedRegex(@"\s+")]
	private static partial Regex WhitespaceRegex();
}
