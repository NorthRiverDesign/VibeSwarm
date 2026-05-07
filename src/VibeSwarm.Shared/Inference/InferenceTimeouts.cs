namespace VibeSwarm.Shared.Inference;

public static class InferenceTimeouts
{
	public static TimeSpan StandardIdeaActionTimeout { get; } = TimeSpan.FromMinutes(5);

	public static TimeSpan InferenceIdeaActionTimeout { get; } = TimeSpan.FromMinutes(60);

	/// <summary>
	/// Maximum time to wait for the first token from a local model.
	/// Extended to accommodate slow devices (e.g. Raspberry Pi) where model loading can take many minutes.
	/// </summary>
	public static TimeSpan LocalInitialResponseTimeout { get; } = TimeSpan.FromMinutes(60);

	/// <summary>
	/// Maximum idle gap between tokens once generation is actively underway.
	/// Extended for slow hardware that may generate only a few tokens per minute.
	/// </summary>
	public static TimeSpan LocalStreamInactivityTimeout { get; } = TimeSpan.FromMinutes(30);

	/// <summary>
	/// Wall-clock cap for an entire local inference request.
	/// Extended to allow long completions on ARM/embedded hardware.
	/// </summary>
	public static TimeSpan LocalGenerationTimeout { get; } = TimeSpan.FromMinutes(120);

	/// <summary>
	/// Maximum idle gap between tokens after generation has visibly started.
	/// Shorter than <see cref="LocalStreamInactivityTimeout"/> so that genuine mid-generation
	/// stalls (e.g. OOM, process crash) are reported quickly rather than after the full
	/// inactivity window. On any hardware actively generating tokens the inter-token gap
	/// should be well under this value.
	/// </summary>
	public static TimeSpan LocalStallDetectionWindow { get; } = TimeSpan.FromMinutes(5);

	public static TimeSpan GetIdeaSuggestionTimeout(bool useInference)
		=> useInference ? InferenceIdeaActionTimeout : StandardIdeaActionTimeout;
}
