namespace VibeSwarm.Shared.Models;

/// <summary>
/// A follow-up message inference wrote for a job whose run has ended, which the user confirms
/// before it is sent. <see cref="Error"/> says why there is no draft.
/// </summary>
public sealed class FollowUpDraft
{
	public string? Text { get; set; }

	public string? ProviderName { get; set; }

	public string? ModelId { get; set; }

	public string? Error { get; set; }
}
