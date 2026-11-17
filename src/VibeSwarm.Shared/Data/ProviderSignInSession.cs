using System.ComponentModel.DataAnnotations;
using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Shared.Data;

public enum ProviderSignInState
{
	/// <summary>The CLI's sign-in command is starting and hasn't printed a sign-in link yet.</summary>
	Starting,

	/// <summary>The sign-in link (and device code or paste box) is ready for the user.</summary>
	WaitingForUser,

	/// <summary>A pasted code was sent to the CLI, which is exchanging it for a login.</summary>
	Verifying,

	Succeeded,
	Failed,
	Cancelled
}

/// <summary>
/// One run of a provider CLI's own sign-in command on the host, relayed to the web UI: the link
/// to open, the one-time code to enter there, or a box to paste the code the sign-in page shows.
/// </summary>
public class ProviderSignInSession
{
	public Guid Id { get; set; }
	public ProviderType ProviderType { get; set; }
	public ProviderSignInState State { get; set; }

	/// <summary>The page the user opens to sign in.</summary>
	public string? SignInUrl { get; set; }

	/// <summary>A device code the user enters on <see cref="SignInUrl"/> (GitHub's device flow).</summary>
	public string? UserCode { get; set; }

	/// <summary>Whether the sign-in page shows a code that must be pasted back here to finish.</summary>
	public bool NeedsCode { get; set; }

	/// <summary>What the CLI reported when it finished, such as the account it signed in as.</summary>
	public string? Message { get; set; }

	public string? ErrorMessage { get; set; }
	public DateTime StartedAt { get; set; }
	public DateTime? FinishedAt { get; set; }

	public bool IsActive => State is ProviderSignInState.Starting or ProviderSignInState.WaitingForUser or ProviderSignInState.Verifying;
}

public class ProviderSignInCodeRequest
{
	[Required]
	[StringLength(2000, MinimumLength = 1)]
	public string Code { get; set; } = string.Empty;
}
