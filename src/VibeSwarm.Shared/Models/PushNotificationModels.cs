namespace VibeSwarm.Shared.Models;

/// <summary>
/// A browser push subscription and the job events the device wants, as the Settings page saves it.
/// </summary>
public class PushSubscriptionRequest
{
	public string Endpoint { get; set; } = string.Empty;

	public string P256dh { get; set; } = string.Empty;

	public string Auth { get; set; } = string.Empty;

	public string? DeviceName { get; set; }

	/// <summary>
	/// The endpoint this one replaces when the browser renewed its subscription, so the device keeps
	/// its switches.
	/// </summary>
	public string? PreviousEndpoint { get; set; }

	/// <summary>Null keeps the saved choice (or the default, on). Same for the two below.</summary>
	public bool? NotifyOnCompleted { get; set; }

	public bool? NotifyOnFailed { get; set; }

	public bool? NotifyOnInputRequired { get; set; }
}

/// <summary>Which job events reach one device.</summary>
public class PushDeviceSettings
{
	public string Endpoint { get; set; } = string.Empty;

	public string? DeviceName { get; set; }

	public bool NotifyOnCompleted { get; set; } = true;

	public bool NotifyOnFailed { get; set; } = true;

	public bool NotifyOnInputRequired { get; set; } = true;
}

public class PushEndpointRequest
{
	public string Endpoint { get; set; } = string.Empty;
}

public class PushTestResult
{
	public bool Success { get; set; }

	public string? Error { get; set; }
}
