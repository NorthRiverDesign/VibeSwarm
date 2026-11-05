using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Services;

/// <summary>Where a project's Lead stands: whether one is set up, its last review and its suggestions.</summary>
public sealed class LeadStatus
{
	/// <summary>Whether the project has a Lead chosen in its settings.</summary>
	public bool IsConfigured { get; set; }

	public bool IsRunning { get; set; }

	public LeadReview? LatestReview { get; set; }

	/// <summary>Ideas the Lead suggested that wait for the owner's approval, best first.</summary>
	public List<Idea> Suggestions { get; set; } = [];
}

/// <summary>
/// A project's Lead: a read-only pass over the repository and its recent history that suggests
/// what to build next. Its ideas wait for the owner's approval before anything runs them.
/// </summary>
public interface ILeadService
{
	Task<LeadStatus> GetStatusAsync(Guid projectId, CancellationToken cancellationToken = default);

	Task<IReadOnlyList<LeadReview>> GetReviewsAsync(Guid projectId, CancellationToken cancellationToken = default);

	/// <summary>Starts a review in the background, or returns the one already running.</summary>
	Task<LeadReview> StartReviewAsync(Guid projectId, CancellationToken cancellationToken = default);

	Task CancelReviewAsync(Guid projectId, CancellationToken cancellationToken = default);

	/// <summary>Moves a suggestion into the Ideas queue, after the ideas already there.</summary>
	Task<Idea?> ApproveSuggestionAsync(Guid ideaId, CancellationToken cancellationToken = default);

	/// <summary>Turns a suggestion down. The Lead is told not to suggest it again.</summary>
	Task DismissSuggestionAsync(Guid ideaId, CancellationToken cancellationToken = default);
}
