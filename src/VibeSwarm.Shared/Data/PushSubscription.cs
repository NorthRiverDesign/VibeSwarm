using System.ComponentModel.DataAnnotations;

namespace VibeSwarm.Shared.Data;

/// <summary>
/// One browser or installed app that receives push notifications about jobs. The endpoint and
/// keys come from the browser's <c>PushManager.subscribe()</c>; the switches pick which job events
/// reach this device.
/// </summary>
public class PushSubscription
{
	public const int EndpointMaxLength = 500;

	public Guid Id { get; set; }

	/// <summary>The user who turned notifications on, so their devices go when they do.</summary>
	public Guid? UserId { get; set; }

	/// <summary>The push service URL the browser handed out for this device.</summary>
	[Required]
	[StringLength(EndpointMaxLength)]
	public string Endpoint { get; set; } = string.Empty;

	/// <summary>The browser's P-256 public key, base64url.</summary>
	[Required]
	[StringLength(200)]
	public string P256dh { get; set; } = string.Empty;

	/// <summary>The browser's authentication secret, base64url.</summary>
	[Required]
	[StringLength(100)]
	public string Auth { get; set; } = string.Empty;

	/// <summary>A short label such as "iPhone" or "Chrome on Mac".</summary>
	[StringLength(100)]
	public string? DeviceName { get; set; }

	public bool NotifyOnCompleted { get; set; } = true;

	public bool NotifyOnFailed { get; set; } = true;

	public bool NotifyOnInputRequired { get; set; } = true;

	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
