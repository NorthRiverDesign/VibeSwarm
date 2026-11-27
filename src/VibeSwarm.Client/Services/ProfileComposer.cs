using VibeSwarm.Shared.Data;

namespace VibeSwarm.Client.Services;

/// <summary>
/// Lets a profile picker anywhere in the app add a profile without leaving the form it sits in.
/// The dialog itself is rendered once, by the layout and outside every form: a profile form
/// nested inside a picker's own form would submit that form too.
/// </summary>
public sealed class ProfileComposer
{
	private readonly List<Profile> _added = [];
	private TaskCompletionSource<Profile?>? _pending;

	public bool IsOpen => _pending != null;

	/// <summary>Which profiles the picker asking can take; the dialog offers only providers that fit.</summary>
	public Func<Profile, bool>? Allowed { get; private set; }

	/// <summary>Profiles added since the app loaded, so a picker whose list is older can still offer them.</summary>
	public IReadOnlyList<Profile> Added => _added;

	public event Action? Changed;

	/// <summary>Opens the dialog and completes with the profile that was added, or null when it was closed.</summary>
	public Task<Profile?> NewAsync(Func<Profile, bool>? allowed)
	{
		_pending?.TrySetResult(null);
		_pending = new TaskCompletionSource<Profile?>();
		Allowed = allowed;
		Changed?.Invoke();
		return _pending.Task;
	}

	public void Complete(Profile? added)
	{
		var pending = _pending;
		if (pending == null)
		{
			return;
		}

		_pending = null;
		Allowed = null;
		if (added != null)
		{
			_added.Add(added);
		}

		Changed?.Invoke();
		pending.TrySetResult(added);
	}
}
