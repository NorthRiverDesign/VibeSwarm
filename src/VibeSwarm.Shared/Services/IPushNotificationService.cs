using VibeSwarm.Shared.Models;

namespace VibeSwarm.Shared.Services;

/// <summary>
/// Turns push notifications about jobs on and off for one device, identified by its push endpoint.
/// </summary>
public interface IPushNotificationService
{
	/// <summary>The server's VAPID public key, base64url, for <c>PushManager.subscribe()</c>.</summary>
	Task<string> GetPublicKeyAsync(CancellationToken cancellationToken = default);

	/// <summary>The saved switches for a device, or null when the server doesn't know it.</summary>
	Task<PushDeviceSettings?> GetDeviceAsync(string endpoint, CancellationToken cancellationToken = default);

	Task<PushDeviceSettings> SaveDeviceAsync(PushSubscriptionRequest request, CancellationToken cancellationToken = default);

	Task DeleteDeviceAsync(string endpoint, CancellationToken cancellationToken = default);

	Task<PushTestResult> SendTestAsync(string endpoint, CancellationToken cancellationToken = default);
}
