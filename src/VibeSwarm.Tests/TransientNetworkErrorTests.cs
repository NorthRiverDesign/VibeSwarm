using System.Net;
using VibeSwarm.Client.Services;

namespace VibeSwarm.Tests;

public sealed class TransientNetworkErrorTests
{
	public static TheoryData<Exception, bool> Cases => new()
	{
		// Safari's fetch failure when the connection drops: no status code at all.
		{ new HttpRequestException("TypeError: Load failed"), true },
		{ new HttpRequestException("Server error", null, HttpStatusCode.BadGateway), true },
		{ new HttpRequestException("Busy", null, HttpStatusCode.TooManyRequests), true },
		{ new InvalidOperationException("wrapped", new HttpRequestException("TypeError: Load failed")), true },
		{ new TimeoutException(), true },
		{ new HttpRequestException("Missing", null, HttpStatusCode.NotFound), false },
		{ new HttpRequestException("Signed out", null, HttpStatusCode.Unauthorized), false },
		{ new NullReferenceException(), false },
	};

	[Theory]
	[MemberData(nameof(Cases))]
	public void Matches_OnlyRetriesConnectionAndServerFailures(Exception exception, bool expected)
	{
		Assert.Equal(expected, TransientNetworkError.Matches(exception));
	}
}
