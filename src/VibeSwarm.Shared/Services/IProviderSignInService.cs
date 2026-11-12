using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Shared.Services;

/// <summary>
/// Signs a provider CLI on the host in again from the web UI, by running the CLI's own sign-in
/// command and relaying its link and codes. One sign-in per provider type at a time.
/// </summary>
public interface IProviderSignInService
{
	/// <summary>Starts a sign-in, or returns the one already in progress for this provider.</summary>
	Task<ProviderSignInSession> StartAsync(ProviderType providerType, CancellationToken cancellationToken = default);

	/// <summary>The latest sign-in for this provider, or null when none has run since VibeSwarm started.</summary>
	Task<ProviderSignInSession?> GetAsync(ProviderType providerType, CancellationToken cancellationToken = default);

	/// <summary>Passes the code shown by the sign-in page to the waiting CLI.</summary>
	Task<ProviderSignInSession> SubmitCodeAsync(ProviderType providerType, string code, CancellationToken cancellationToken = default);

	/// <summary>Stops a sign-in in progress. Returns the latest sign-in, or null when there is none.</summary>
	Task<ProviderSignInSession?> CancelAsync(ProviderType providerType, CancellationToken cancellationToken = default);
}
