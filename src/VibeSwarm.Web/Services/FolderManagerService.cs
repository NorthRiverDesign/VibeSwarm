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
	internal const int MaxPreviousReportLength = 8000;
	private readonly VibeSwarmDbContext _db;
	private readonly IInferenceService _inference;
	private readonly IGitCommandExecutor _git;
	private readonly ILogger<FolderManagerService> _logger;
	private readonly ProjectContextBuilder _context;

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
		_context = new ProjectContextBuilder(db, git, logger);
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
			sections.Add(await _context.BuildProjectSectionAsync(project, ProjectContextOptions.FolderReport, cancellationToken));
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

	private static string FormatDate(DateTime value) => ProjectContextBuilder.FormatDate(value);

	private static string Truncate(string text, int maxLength) => ProjectContextBuilder.Truncate(text, maxLength);

	[GeneratedRegex(@"<think>.*?(</think>|$)", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
	private static partial Regex ThinkBlockRegex();
}
