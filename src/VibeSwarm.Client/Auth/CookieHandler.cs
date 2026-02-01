using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace VibeSwarm.Client.Auth;

/// <summary>
/// HTTP message handler that ensures browser fetch API includes credentials (cookies)
/// with every request. This is critical for iOS Safari which has stricter cookie policies
/// and requires explicit credential inclusion for same-origin requests after WebAssembly migration.
/// </summary>
public class CookieHandler : DelegatingHandler
{
	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		// Set browser fetch credentials mode to 'include' to send cookies with every request
		// This is essential for cookie-based authentication in Blazor WebAssembly on iOS Safari
		request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);

		// Disable browser caching for API requests to prevent stale auth state
		request.SetBrowserRequestCache(BrowserRequestCache.NoStore);

		return await base.SendAsync(request, cancellationToken);
	}
}
