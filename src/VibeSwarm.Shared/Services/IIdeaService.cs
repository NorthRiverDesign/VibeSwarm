using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;

namespace VibeSwarm.Shared.Services;

public interface IIdeaService
{
	/// <summary>
	/// Gets all ideas for a project ordered by SortOrder
	/// </summary>
	Task<IEnumerable<Idea>> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Gets a paged set of ideas for a project ordered by SortOrder.
	/// Includes aggregate counts needed by the Ideas panel.
	/// </summary>
	Task<ProjectIdeasListResult> GetPagedByProjectIdAsync(Guid projectId, int page = 1, int pageSize = 10, CancellationToken cancellationToken = default);

	/// <summary>
	/// Gets an idea by its ID
	/// </summary>
	Task<Idea?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

	/// <summary>
	/// Creates a new idea
	/// </summary>
	Task<Idea> CreateAsync(Idea idea, CancellationToken cancellationToken = default);

	/// <summary>
	/// Creates a new idea with optional uploaded attachments.
	/// </summary>
	Task<Idea> CreateAsync(CreateIdeaRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	/// Updates an existing idea
	/// </summary>
	Task<Idea> UpdateAsync(Idea idea, CancellationToken cancellationToken = default);

	/// <summary>
	/// Deletes an idea
	/// </summary>
	Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

	/// <summary>
	/// Gets the next unprocessed idea for a project
	/// </summary>
	Task<Idea?> GetNextUnprocessedAsync(Guid projectId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Converts an idea into a job. Uses the default provider/model and current branch.
	/// The idea is expanded into a feature spec using a generic prompt.
	/// </summary>
	/// <param name="ideaId">The idea to convert</param>
	/// <returns>The created job, or null if the idea doesn't exist</returns>
	Task<Job?> ConvertToJobAsync(Guid ideaId, IdeaProcessingOptions? options = null, CancellationToken cancellationToken = default);

	/// <summary>
	/// Marks an idea as completed and removes it (called when the associated job completes)
	/// </summary>
	Task<bool> CompleteIdeaFromJobAsync(Guid jobId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Handles job completion for an idea. If successful, removes the idea.
	/// If failed or cancelled, resets the idea for potential retry.
	/// </summary>
	/// <param name="jobId">The job that completed</param>
	/// <param name="success">Whether the job completed successfully</param>
	/// <returns>True if an idea was found and handled</returns>
	Task<bool> HandleJobCompletionAsync(Guid jobId, bool success, CancellationToken cancellationToken = default);

	/// <summary>
	/// Gets the idea associated with a job (if any)
	/// </summary>
	Task<Idea?> GetByJobIdAsync(Guid jobId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Gets a single persisted idea attachment by ID.
	/// </summary>
	Task<IdeaAttachment?> GetAttachmentAsync(Guid attachmentId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Starts auto-processing of ideas for a project
	/// </summary>
	/// <param name="projectId">The project to start processing</param>
	/// <param name="autoCommit">Whether to auto-commit changes when jobs complete</param>
	Task StartProcessingAsync(Guid projectId, IdeaProcessingOptions? options = null, CancellationToken cancellationToken = default);

	/// <summary>
	/// Stops auto-processing of ideas for a project
	/// </summary>
	Task StopProcessingAsync(Guid projectId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Checks if auto-processing is active for a project
	/// </summary>
	Task<bool> IsProcessingActiveAsync(Guid projectId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Processes the next idea in the queue if processing is active and no idea is currently being processed.
	/// This is called by the background worker service.
	/// </summary>
	/// <param name="projectId">The project to process</param>
	/// <returns>True if an idea was processed, false otherwise</returns>
	Task<bool> ProcessNextIdeaIfReadyAsync(Guid projectId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Gets all projects that have Ideas auto-processing active
	/// </summary>
	Task<IEnumerable<Guid>> GetActiveProcessingProjectsAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// Scans all ideas with IsProcessing=true and resets any that are orphaned —
	/// either because their linked job is in a terminal state or the job no longer exists.
	/// Also resets ideas stuck with IsProcessing=true but no JobId (partial conversion failure).
	/// Called periodically by the background service as a safety net.
	/// </summary>
	Task RecoverStuckIdeasAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// Reorders ideas for a project
	/// </summary>
	Task ReorderIdeasAsync(Guid projectId, IEnumerable<Guid> ideaIdsInOrder, CancellationToken cancellationToken = default);

	/// <summary>
	/// Copies an idea to another project
	/// </summary>
	/// <param name="ideaId">The idea to copy</param>
	/// <param name="targetProjectId">The target project to copy the idea to</param>
	Task<Idea> CopyToProjectAsync(Guid ideaId, Guid targetProjectId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Moves an idea to another project
	/// </summary>
	/// <param name="ideaId">The idea to move</param>
	/// <param name="targetProjectId">The target project to move the idea to</param>
	Task<Idea> MoveToProjectAsync(Guid ideaId, Guid targetProjectId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Gets a summary of ideas processing status across all projects
	/// </summary>
	Task<GlobalIdeasProcessingStatus> GetGlobalProcessingStatusAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// Gets the global queue snapshot used by the navigation queue panel.
	/// Includes currently running jobs and upcoming ideas across active projects.
	/// </summary>
	Task<GlobalQueueSnapshot> GetGlobalQueueSnapshotAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// Starts auto-processing of ideas for all projects that have unprocessed ideas
	/// </summary>
	Task StartAllProcessingAsync(IdeaProcessingOptions? options = null, CancellationToken cancellationToken = default);

	/// <summary>
	/// Stops auto-processing of ideas for all currently processing projects
	/// </summary>
	Task StopAllProcessingAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// Scans the project directory and suggests feature ideas or improvements.
	/// Uses either an inference provider or a configured coding provider based on the request.
	/// Returns a <see cref="SuggestIdeasResult"/> that always carries a diagnostic stage and message,
	/// even on failure, so the caller can surface precise feedback to the user.
	/// </summary>
	/// <param name="projectId">The project to analyze</param>
	/// <param name="request">Optional provider/count overrides for the suggestion request</param>
	Task<SuggestIdeasResult> SuggestIdeasFromCodebaseAsync(Guid projectId, SuggestIdeasRequest? request = null, CancellationToken cancellationToken = default);
}
