using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Validation;

namespace VibeSwarm.Shared.Models;

public sealed class IdeaProcessingOptions
{
	public AutoCommitMode AutoCommitMode { get; set; } = AutoCommitMode.Off;

	public Guid? ProviderId { get; set; }

	[StringLength(ValidationLimits.JobScheduleModelIdMaxLength)]
	public string? ModelId { get; set; }

	/// <summary>
	/// The auto-pilot loop the job is created for. Set on the server only, so the job is saved
	/// already linked instead of being patched after it may have started.
	/// </summary>
	[JsonIgnore]
	public Guid? IterationLoopId { get; set; }

	/// <summary>Replaces the project's commit mode for the job. Set on the server only.</summary>
	[JsonIgnore]
	public AutoCommitMode? CommitModeOverride { get; set; }
}
