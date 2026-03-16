namespace VibeSwarm.Client.Services;

/// <summary>
/// Coordinates idea-related toast timing so a transient idea update toast can be suppressed
/// when the same idea immediately starts as a job.
/// </summary>
public sealed class IdeaToastCoordinator
{
	private readonly object _lock = new();
	private readonly Dictionary<string, IdeaStartRecord> _recentIdeaStarts = new(StringComparer.Ordinal);
	private readonly TimeProvider _timeProvider;
	private readonly TimeSpan _ideaUpdateDelay;
	private readonly TimeSpan _retentionWindow;

	public IdeaToastCoordinator(
		TimeProvider? timeProvider = null,
		TimeSpan? ideaUpdateDelay = null,
		TimeSpan? retentionWindow = null)
	{
		_timeProvider = timeProvider ?? TimeProvider.System;
		_ideaUpdateDelay = ideaUpdateDelay ?? TimeSpan.FromMilliseconds(750);
		_retentionWindow = retentionWindow ?? TimeSpan.FromSeconds(10);
	}

	public void RegisterIdeaStarted(string ideaId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(ideaId);

		lock (_lock)
		{
			var now = _timeProvider.GetUtcNow();
			CleanupExpiredStarts(now);

			var version = _recentIdeaStarts.TryGetValue(ideaId, out var existing)
				? existing.Version + 1
				: 1;

			_recentIdeaStarts[ideaId] = new IdeaStartRecord(now, version);
		}
	}

	public async Task<bool> ShouldShowIdeaUpdatedAsync(string ideaId, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(ideaId);

		var observedAt = _timeProvider.GetUtcNow();
		var observedVersion = 0;

		lock (_lock)
		{
			CleanupExpiredStarts(observedAt);
			if (_recentIdeaStarts.TryGetValue(ideaId, out var existing))
			{
				observedVersion = existing.Version;
			}
		}

		await Task.Delay(_ideaUpdateDelay, _timeProvider, cancellationToken);

		lock (_lock)
		{
			var now = _timeProvider.GetUtcNow();
			CleanupExpiredStarts(now);

			if (!_recentIdeaStarts.TryGetValue(ideaId, out var existing))
			{
				return true;
			}

			if (existing.Version != observedVersion)
			{
				return false;
			}

			return observedAt - existing.RecordedAt > _ideaUpdateDelay;
		}
	}

	private void CleanupExpiredStarts(DateTimeOffset now)
	{
		var cutoff = now - _retentionWindow;
		foreach (var staleIdeaId in _recentIdeaStarts
			.Where(entry => entry.Value.RecordedAt < cutoff)
			.Select(entry => entry.Key)
			.ToList())
		{
			_recentIdeaStarts.Remove(staleIdeaId);
		}
	}

	private readonly record struct IdeaStartRecord(DateTimeOffset RecordedAt, int Version);
}
