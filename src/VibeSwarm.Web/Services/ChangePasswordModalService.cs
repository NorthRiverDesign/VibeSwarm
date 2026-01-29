namespace VibeSwarm.Web.Services;

/// <summary>
/// Service to coordinate the Change Password modal between LoginDisplay and MainLayout.
/// This is needed because modals rendered inside the sidebar get trapped by the transform stacking context.
/// </summary>
public class ChangePasswordModalService
{
	public event Action? OnShowModal;
	public event Action? OnHideModal;

	public void Show()
	{
		OnShowModal?.Invoke();
	}

	public void Hide()
	{
		OnHideModal?.Invoke();
	}
}
