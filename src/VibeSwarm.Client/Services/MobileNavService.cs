namespace VibeSwarm.Client.Services;

/// <summary>
/// Coordinates the mobile sidebar menu state between the bottom tab bar and MainLayout.
/// </summary>
public sealed class MobileNavService
{
	public event Action? OnMenuToggleRequested;
	public event Action? OnMenuCloseRequested;

	public void RequestMenuToggle()
	{
		OnMenuToggleRequested?.Invoke();
	}

	public void RequestMenuClose()
	{
		OnMenuCloseRequested?.Invoke();
	}
}
