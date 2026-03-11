using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace VibeSwarm.Shared.Data;

public class JobProviderAttempt
{
	public Guid Id { get; set; } = Guid.NewGuid();

	public Guid JobId { get; set; }

	[JsonIgnore]
	public Job? Job { get; set; }

	public Guid ProviderId { get; set; }

	[StringLength(100)]
	public string ProviderName { get; set; } = string.Empty;

	[StringLength(200)]
	public string? ModelId { get; set; }

	public int AttemptOrder { get; set; }

	[StringLength(100)]
	public string Reason { get; set; } = string.Empty;

	public bool WasSuccessful { get; set; }

	public DateTime AttemptedAt { get; set; } = DateTime.UtcNow;
}