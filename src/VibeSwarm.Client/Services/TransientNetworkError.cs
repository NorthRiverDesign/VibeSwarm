using System.Net;

namespace VibeSwarm.Client.Services;

// A phone drops its connection often (weak signal, the app resuming, a server restart), so a failed
// request or a 5xx is worth retrying instead of stranding the page on an error.
public static class TransientNetworkError
{
	public static bool Matches(Exception exception)
	{
		for (var current = exception; current is not null; current = current.InnerException)
		{
			if (current is HttpRequestException httpError)
			{
				return httpError.StatusCode is null
					or HttpStatusCode.RequestTimeout
					or HttpStatusCode.TooManyRequests
					or >= HttpStatusCode.InternalServerError;
			}

			if (current is TimeoutException)
			{
				return true;
			}
		}

		return false;
	}
}
