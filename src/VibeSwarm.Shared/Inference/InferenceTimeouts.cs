namespace VibeSwarm.Shared.Inference;

public static class InferenceTimeouts
{
	public static TimeSpan StandardIdeaActionTimeout { get; } = TimeSpan.FromMinutes(5);

	public static TimeSpan InferenceIdeaActionTimeout { get; } = TimeSpan.FromMinutes(60);

	public static TimeSpan LocalInitialResponseTimeout { get; } = TimeSpan.FromMinutes(30);

	public static TimeSpan LocalStreamInactivityTimeout { get; } = TimeSpan.FromMinutes(15);

	public static TimeSpan LocalGenerationTimeout { get; } = TimeSpan.FromMinutes(60);

	public static TimeSpan GetIdeaActionTimeout(bool useInference)
		=> useInference ? InferenceIdeaActionTimeout : StandardIdeaActionTimeout;
}
