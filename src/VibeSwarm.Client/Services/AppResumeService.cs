namespace VibeSwarm.Client.Services;

/// <summary>
/// Raised when the app comes back to the foreground. A phone suspends a backgrounded tab or
/// Home Screen app, so live updates sent meanwhile never arrive; pages reload what they show.
/// </summary>
public sealed class AppResumeService
{
	public event Func<Task>? Resumed;

	public async Task NotifyResumedAsync()
	{
		var handlers = Resumed;
		if (handlers == null)
		{
			return;
		}

		foreach (var resumeHandler in handlers.GetInvocationList().Cast<Func<Task>>())
		{
			try
			{
				await resumeHandler();
			}
			catch
			{
			}
		}
	}
}
