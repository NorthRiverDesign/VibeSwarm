using Microsoft.JSInterop;

namespace VibeSwarm.Web.Services;

/// <summary>
/// Provides a static method that can be called from JavaScript to verify
/// that Blazor interactivity is working after the app returns from background.
/// This is particularly important for iOS PWAs where event handlers can become stale.
/// </summary>
public static class InteractivityPingService
{
	/// <summary>
	/// Simple ping method to verify circuit interactivity.
	/// Called from JavaScript to test if .NET can respond to JS interop calls.
	/// </summary>
	[JSInvokable("InteractivityPing")]
	public static bool Ping()
	{
		return true;
	}
}
