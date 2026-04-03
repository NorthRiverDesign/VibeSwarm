namespace VibeSwarm.Client.Services;

public sealed class QueuePanelStateService
{
	public event Func<Task>? RefreshRequested;

	public async Task RequestRefreshAsync()
	{
		var handlers = RefreshRequested;
		if (handlers == null)
		{
			return;
		}

		foreach (var refreshHandler in handlers.GetInvocationList().Cast<Func<Task>>())
		{
			try
			{
				await refreshHandler();
			}
			catch
			{
			}
		}
	}
}
