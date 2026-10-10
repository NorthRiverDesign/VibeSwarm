using Microsoft.AspNetCore.SignalR.Client;

namespace VibeSwarm.Client.Services;

// SignalR stops reconnecting once a fixed delay list runs out, which a server restart (often 30-40s
// here) outlasts, leaving the page without live updates until a reload. Keep trying instead.
public sealed class KeepReconnectingPolicy : IRetryPolicy
{
	private static readonly TimeSpan[] FirstDelays =
		[TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

	private static readonly TimeSpan SteadyDelay = TimeSpan.FromSeconds(15);

	public TimeSpan? NextRetryDelay(RetryContext retryContext)
		=> retryContext.PreviousRetryCount < FirstDelays.Length
			? FirstDelays[retryContext.PreviousRetryCount]
			: SteadyDelay;
}
