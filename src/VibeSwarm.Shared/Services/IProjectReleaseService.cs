namespace VibeSwarm.Shared.Services;

/// <summary>
/// Deploys a project by merging its development branch into its release branch, which CI/CD
/// ships from. Deploys are manual: the user batches changes on development and sends them out.
/// </summary>
public interface IProjectReleaseService
{
	Task<ProjectReleaseStatus> GetStatusAsync(Guid projectId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Merges the development branch, as it is on origin, into the release branch and pushes it.
	/// When the two conflict, nothing is pushed and a job is queued for a provider to resolve the
	/// conflicts on the development branch; deploying again afterwards goes through cleanly.
	/// </summary>
	Task<DeployResult> DeployAsync(Guid projectId, CancellationToken cancellationToken = default);
}

public sealed class ProjectReleaseStatus
{
	public string? DevelopmentBranch { get; set; }

	public string? ReleaseBranch { get; set; }

	public bool IsConfigured => !string.IsNullOrWhiteSpace(DevelopmentBranch) && !string.IsNullOrWhiteSpace(ReleaseBranch);

	/// <summary>Commits on the development branch the release branch doesn't have yet, newest first.</summary>
	public List<ReleaseChange> PendingChanges { get; set; } = [];

	/// <summary>How many commits are waiting in total; <see cref="PendingChanges"/> lists only the latest.</summary>
	public int PendingCount { get; set; }

	/// <summary>The latest commit on the release branch: the last thing deployed.</summary>
	public ReleaseChange? ReleaseHead { get; set; }

	/// <summary>The job resolving conflicts between the branches, while it is queued or running.</summary>
	public Guid? ResolvingJobId { get; set; }

	/// <summary>Why the branches couldn't be read, such as a branch that doesn't exist yet.</summary>
	public string? Error { get; set; }
}

public sealed class ReleaseChange
{
	public string Hash { get; set; } = string.Empty;

	public string Subject { get; set; } = string.Empty;

	public DateTime CommittedAt { get; set; }
}

public enum DeployOutcome
{
	Deployed,
	NothingToDeploy,
	ResolvingConflicts,
	Failed
}

public sealed class DeployResult
{
	public DeployOutcome Outcome { get; set; }

	public string Message { get; set; } = string.Empty;

	public string? CommitHash { get; set; }

	/// <summary>The conflict-resolution job, when the deploy handed conflicts to a provider.</summary>
	public Guid? JobId { get; set; }
}
