using System.ComponentModel.DataAnnotations;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Validation;

namespace VibeSwarm.Shared.Data;

public enum JobScheduleFrequency
{
	Hourly = 0,
	Daily = 1,
	Weekly = 2,
	Monthly = 3
}

public enum JobScheduleExecutionTarget
{
	Provider = 0,
	TeamRole = 1
}

public class JobSchedule : IValidatableObject
{
	public Guid Id { get; set; }

	public Guid ProjectId { get; set; }
	public Project? Project { get; set; }

	public JobScheduleExecutionTarget ExecutionTarget { get; set; } = JobScheduleExecutionTarget.Provider;

	public Guid? ProviderId { get; set; }
	public Provider? Provider { get; set; }

	public Guid? TeamRoleId { get; set; }
	public TeamRole? TeamRole { get; set; }

	[Required]
	[StringLength(ValidationLimits.JobSchedulePromptMaxLength, MinimumLength = 1)]
	public string Prompt { get; set; } = string.Empty;

	[StringLength(ValidationLimits.JobScheduleModelIdMaxLength)]
	public string? ModelId { get; set; }

	public JobScheduleFrequency Frequency { get; set; } = JobScheduleFrequency.Daily;

	[Range(0, 23)]
	public int HourUtc { get; set; } = 9;

	[Range(0, 59)]
	public int MinuteUtc { get; set; }

	public DayOfWeek WeeklyDay { get; set; } = DayOfWeek.Monday;

	[Range(1, 31)]
	public int DayOfMonth { get; set; } = 1;

	public bool IsEnabled { get; set; } = true;

	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
	public DateTime? UpdatedAt { get; set; }
	public DateTime NextRunAtUtc { get; set; } = DateTime.UtcNow;
	public DateTime? LastRunAtUtc { get; set; }

	[StringLength(ValidationLimits.JobScheduleLastErrorMaxLength)]
	public string? LastError { get; set; }

	public ICollection<Job> Jobs { get; set; } = new List<Job>();

	public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
	{
		if (ProjectId == Guid.Empty)
		{
			yield return new ValidationResult("A project is required.", [nameof(ProjectId)]);
		}

		if (ExecutionTarget == JobScheduleExecutionTarget.Provider && (!ProviderId.HasValue || ProviderId == Guid.Empty))
		{
			yield return new ValidationResult("A provider is required.", [nameof(ProviderId)]);
		}

		if (ExecutionTarget == JobScheduleExecutionTarget.TeamRole && (!TeamRoleId.HasValue || TeamRoleId == Guid.Empty))
		{
			yield return new ValidationResult("A team member is required.", [nameof(TeamRoleId)]);
		}

		if (string.IsNullOrWhiteSpace(Prompt))
		{
			yield return new ValidationResult("A prompt is required.", [nameof(Prompt)]);
		}
	}
}
