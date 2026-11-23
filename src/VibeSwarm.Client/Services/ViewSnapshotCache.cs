using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace VibeSwarm.Client.Services;

/// <summary>
/// Remembers what each page last showed, so opening it again draws that at once while it reloads
/// in the background, instead of sitting behind a spinner. A page always fetches fresh data; the
/// snapshot only fills the wait. Held in memory for the life of the app (a scoped service lives
/// that long in WebAssembly), and logging out reloads the app, which clears it.
/// </summary>
public sealed class ViewSnapshotCache
{
	private readonly ConcurrentDictionary<string, object> _snapshots = new(StringComparer.Ordinal);

	public bool TryGet<T>(string key, [NotNullWhen(true)] out T? snapshot) where T : class
	{
		if (_snapshots.TryGetValue(key, out var value) && value is T typed)
		{
			snapshot = typed;
			return true;
		}

		snapshot = null;
		return false;
	}

	public void Set<T>(string key, T snapshot) where T : class => _snapshots[key] = snapshot;

	public void Remove(string key) => _snapshots.TryRemove(key, out _);
}
