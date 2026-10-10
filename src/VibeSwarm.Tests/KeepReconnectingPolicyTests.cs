using Microsoft.AspNetCore.SignalR.Client;
using VibeSwarm.Client.Services;

namespace VibeSwarm.Tests;

public sealed class KeepReconnectingPolicyTests
{
	[Theory]
	[InlineData(0, 0)]
	[InlineData(1, 2)]
	[InlineData(3, 10)]
	[InlineData(4, 15)]
	[InlineData(500, 15)]
	public void NextRetryDelay_NeverGivesUp(long previousRetryCount, int expectedSeconds)
	{
		var delay = new KeepReconnectingPolicy().NextRetryDelay(new RetryContext { PreviousRetryCount = previousRetryCount });

		Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
	}
}
