using System.Collections.Concurrent;

namespace VibeSwarm.Web.Services;

/// <summary>
/// Projects whose queued jobs must not start yet, each until a deadline. Kept in memory
/// on purpose: a hold only lives as long as someone is actively editing, and a lapsed or
/// forgotten one must never leave an unattended project stopped.
/// </summary>
public sealed class ProjectQueueHolds
{
	public static readonly TimeSpan HoldDuration = TimeSpan.FromMinutes(10);

	private readonly ConcurrentDictionary<Guid, DateTime> _heldUntil = new();
	private readonly TimeProvider _timeProvider;

	public ProjectQueueHolds(TimeProvider? timeProvider = null)
	{
		_timeProvider = timeProvider ?? TimeProvider.System;
	}

	/// <summary>Starts or renews the hold on a project and returns when it lapses.</summary>
	public DateTime Hold(Guid projectId)
	{
		var heldUntil = _timeProvider.GetUtcNow().UtcDateTime + HoldDuration;
		_heldUntil[projectId] = heldUntil;
		return heldUntil;
	}

	public bool Release(Guid projectId) => _heldUntil.TryRemove(projectId, out _);

	public DateTime? GetHeldUntil(Guid projectId)
	{
		if (!_heldUntil.TryGetValue(projectId, out var heldUntil))
		{
			return null;
		}

		if (heldUntil > _timeProvider.GetUtcNow().UtcDateTime)
		{
			return heldUntil;
		}

		_heldUntil.TryRemove(new KeyValuePair<Guid, DateTime>(projectId, heldUntil));
		return null;
	}

	public bool IsHeld(Guid projectId) => GetHeldUntil(projectId).HasValue;
}
