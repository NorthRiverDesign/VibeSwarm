using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Services;

public interface IIdeaService
{
	/// <summary>
	/// Gets all ideas for a project ordered by SortOrder
	/// </summary>
	Task<IEnumerable<Idea>> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Gets an idea by its ID
	/// </summary>
	Task<Idea?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

	/// <summary>
	/// Creates a new idea
	/// </summary>
	Task<Idea> CreateAsync(Idea idea, CancellationToken cancellationToken = default);

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
	/// <param name="cancellationToken">Cancellation token</param>
	/// <returns>The created job, or null if the idea doesn't exist</returns>
	Task<Job?> ConvertToJobAsync(Guid ideaId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Marks an idea as completed and removes it (called when the associated job completes)
	/// </summary>
	Task<bool> CompleteIdeaFromJobAsync(Guid jobId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Gets the idea associated with a job (if any)
	/// </summary>
	Task<Idea?> GetByJobIdAsync(Guid jobId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Starts auto-processing of ideas for a project
	/// </summary>
	Task StartProcessingAsync(Guid projectId, CancellationToken cancellationToken = default);

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
	/// <param name="cancellationToken">Cancellation token</param>
	/// <returns>True if an idea was processed, false otherwise</returns>
	Task<bool> ProcessNextIdeaIfReadyAsync(Guid projectId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Gets all projects that have Ideas auto-processing active
	/// </summary>
	Task<IEnumerable<Guid>> GetActiveProcessingProjectsAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// Reorders ideas for a project
	/// </summary>
	Task ReorderIdeasAsync(Guid projectId, IEnumerable<Guid> ideaIdsInOrder, CancellationToken cancellationToken = default);

	/// <summary>
	/// Copies an idea to another project
	/// </summary>
	/// <param name="ideaId">The idea to copy</param>
	/// <param name="targetProjectId">The target project to copy the idea to</param>
	/// <param name="cancellationToken">Cancellation token</param>
	/// <returns>The new copied idea</returns>
	Task<Idea> CopyToProjectAsync(Guid ideaId, Guid targetProjectId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Moves an idea to another project
	/// </summary>
	/// <param name="ideaId">The idea to move</param>
	/// <param name="targetProjectId">The target project to move the idea to</param>
	/// <param name="cancellationToken">Cancellation token</param>
	/// <returns>The moved idea</returns>
	Task<Idea> MoveToProjectAsync(Guid ideaId, Guid targetProjectId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Uses AI to expand a brief idea into a detailed specification.
	/// The expanded spec is stored for user review before converting to a job.
	/// </summary>
	/// <param name="ideaId">The idea to expand</param>
	/// <param name="cancellationToken">Cancellation token</param>
	/// <returns>The updated idea with expansion status</returns>
	Task<Idea?> ExpandIdeaAsync(Guid ideaId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Approves an expanded idea specification, allowing it to be converted to a job.
	/// </summary>
	/// <param name="ideaId">The idea to approve</param>
	/// <param name="editedDescription">Optional edited description to use instead of the AI-generated one</param>
	/// <param name="cancellationToken">Cancellation token</param>
	/// <returns>The approved idea</returns>
	Task<Idea?> ApproveExpansionAsync(Guid ideaId, string? editedDescription = null, CancellationToken cancellationToken = default);

	/// <summary>
	/// Rejects an expanded idea specification and resets it for re-expansion or manual editing.
	/// </summary>
	/// <param name="ideaId">The idea to reject</param>
	/// <param name="cancellationToken">Cancellation token</param>
	/// <returns>The reset idea</returns>
	Task<Idea?> RejectExpansionAsync(Guid ideaId, CancellationToken cancellationToken = default);
}
