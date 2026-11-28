using System.ComponentModel.DataAnnotations;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Validation;

namespace VibeSwarm.Shared.Data;

public enum JobScheduleFrequency
{
	Hourly = 0,
	Daily = 1,
	Weekly = 2,
	Monthly = 3,
	Minutes = 4
}

public enum JobScheduleType
{
	RunJob = 0,
	GenerateIdeas = 1
}

public class JobSchedule : IValidatableObject
{
	public Guid Id { get; set; }

	public Guid ProjectId { get; set; }
	public Project? Project { get; set; }

	public JobScheduleType ScheduleType { get; set; } = JobScheduleType.RunJob;

	public Guid? ProviderId { get; set; }
	public Provider? Provider { get; set; }

	/// <summary>
	/// The profile the scheduled job runs on; <see cref="ProviderId"/> and <see cref="ModelId"/>
	/// are copied from it. Null, with no provider either, runs on the project's Build profile.
	/// </summary>
	public Guid? ProfileId { get; set; }

	/// <summary>The name of <see cref="ProfileId"/>. Filled in when a schedule is read; not stored.</summary>
	[System.ComponentModel.DataAnnotations.Schema.NotMapped]
	public string? ProfileName { get; set; }

	public Guid? InferenceProviderId { get; set; }
	public InferenceProvider? InferenceProvider { get; set; }

	[StringLength(ValidationLimits.JobSchedulePromptMaxLength)]
	public string Prompt { get; set; } = string.Empty;

	[StringLength(ValidationLimits.JobScheduleModelIdMaxLength)]
	public string? ModelId { get; set; }

	[Range(ValidationLimits.JobScheduleIdeaCountMin, ValidationLimits.JobScheduleIdeaCountMax)]
	public int IdeaCount { get; set; } = 3;

	public JobScheduleFrequency Frequency { get; set; } = JobScheduleFrequency.Daily;

	[Range(5, 60)]
	public int IntervalMinutes { get; set; } = 15;

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

		// An idea schedule names an inference profile; the server fills in the provider it runs on.
		// (A job schedule may name a profile, or nothing at all to run on the project's Build profile.)
		if (ScheduleType == JobScheduleType.GenerateIdeas && !ProfileId.HasValue && (!InferenceProviderId.HasValue || InferenceProviderId == Guid.Empty))
		{
			yield return new ValidationResult("Choose an inference profile for the ideas.", [nameof(ProfileId)]);
		}

		if (ScheduleType == JobScheduleType.RunJob && string.IsNullOrWhiteSpace(Prompt))
		{
			yield return new ValidationResult("A prompt is required.", [nameof(Prompt)]);
		}

		if (Frequency == JobScheduleFrequency.Minutes && (IntervalMinutes < 5 || IntervalMinutes > 60))
		{
			yield return new ValidationResult("Interval must be between 5 and 60 minutes.", [nameof(IntervalMinutes)]);
		}
	}
}
