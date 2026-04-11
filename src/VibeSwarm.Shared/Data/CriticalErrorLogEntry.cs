using System.ComponentModel.DataAnnotations;
using VibeSwarm.Shared.Validation;

namespace VibeSwarm.Shared.Data;

public class CriticalErrorLogEntry
{
	public Guid Id { get; set; }

	[Required]
	[StringLength(ValidationLimits.CriticalErrorLogFieldMaxLength)]
	public string Source { get; set; } = "server";

	[Required]
	[StringLength(ValidationLimits.CriticalErrorLogFieldMaxLength)]
	public string Category { get; set; } = "unhandled-exception";

	[Required]
	[StringLength(ValidationLimits.CriticalErrorLogFieldMaxLength)]
	public string Severity { get; set; } = "critical";

	[Required]
	[StringLength(ValidationLimits.CriticalErrorLogMessageMaxLength)]
	public string Message { get; set; } = string.Empty;

	[StringLength(ValidationLimits.CriticalErrorLogDetailsMaxLength)]
	public string? Details { get; set; }

	[StringLength(ValidationLimits.CriticalErrorLogTraceIdMaxLength)]
	public string? TraceId { get; set; }

	[StringLength(ValidationLimits.CriticalErrorLogUrlMaxLength)]
	public string? Url { get; set; }

	[StringLength(ValidationLimits.CriticalErrorLogUserAgentMaxLength)]
	public string? UserAgent { get; set; }

	[StringLength(ValidationLimits.CriticalErrorLogFieldMaxLength)]
	public string? RefreshAction { get; set; }

	public bool TriggeredRefresh { get; set; }

	[StringLength(ValidationLimits.CriticalErrorLogMetadataMaxLength)]
	public string? AdditionalDataJson { get; set; }

	public Guid? UserId { get; set; }

	public DateTime CreatedAt { get; set; }
}
