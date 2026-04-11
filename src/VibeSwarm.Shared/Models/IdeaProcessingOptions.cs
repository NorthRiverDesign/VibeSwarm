using System.ComponentModel.DataAnnotations;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Validation;

namespace VibeSwarm.Shared.Models;

public sealed class IdeaProcessingOptions
{
	public AutoCommitMode AutoCommitMode { get; set; } = AutoCommitMode.Off;

	public Guid? ProviderId { get; set; }

	[StringLength(ValidationLimits.JobScheduleModelIdMaxLength)]
	public string? ModelId { get; set; }
}
