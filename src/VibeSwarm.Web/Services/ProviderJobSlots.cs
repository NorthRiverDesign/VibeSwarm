using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;

namespace VibeSwarm.Web.Services;

/// <summary>
/// Which queued jobs a provider can start: each provider runs up to its own
/// <see cref="Shared.Providers.Provider.MaxConcurrentJobs"/> jobs at once, one session each. A
/// job with no provider yet is never held back here. Checkouts are guarded separately, by
/// <see cref="ProjectJobSlots"/>, so two jobs on one provider are always in different projects.
/// </summary>
internal sealed class ProviderJobSlots
{
	private readonly Dictionary<Guid, int> _running = new();
	private readonly IReadOnlyDictionary<Guid, int> _limits;

	/// <param name="runningProviderIds">The provider of each job in flight, once per job.</param>
	/// <param name="limits">Each provider's job limit; a provider missing here runs one job at a time.</param>
	public ProviderJobSlots(IEnumerable<Guid> runningProviderIds, IReadOnlyDictionary<Guid, int> limits)
	{
		_limits = limits;
		foreach (var providerId in runningProviderIds)
		{
			Take(providerId);
		}
	}

	/// <summary>Every provider's job limit, for building slots from the database.</summary>
	public static async Task<Dictionary<Guid, int>> LoadLimitsAsync(VibeSwarmDbContext dbContext, CancellationToken cancellationToken)
		=> await dbContext.Providers
			.AsNoTracking()
			.Select(provider => new { provider.Id, provider.MaxConcurrentJobs })
			.ToDictionaryAsync(provider => provider.Id, provider => provider.MaxConcurrentJobs, cancellationToken);

	/// <summary>The limits of the providers these jobs run on, read from their loaded providers.</summary>
	public static Dictionary<Guid, int> LimitsOf(IEnumerable<Job> jobs)
	{
		var limits = new Dictionary<Guid, int>();
		foreach (var job in jobs)
		{
			if (job.Provider != null)
			{
				limits.TryAdd(job.ProviderId, job.Provider.MaxConcurrentJobs);
			}
		}

		return limits;
	}

	/// <summary>Providers already running as many jobs as they may.</summary>
	public IReadOnlyList<Guid> FullProviderIds => _running
		.Where(entry => entry.Value >= LimitOf(entry.Key))
		.Select(entry => entry.Key)
		.ToList();

	public int LimitOf(Guid providerId) => _limits.TryGetValue(providerId, out var limit)
		? JobConcurrency.Normalize(limit)
		: JobConcurrency.MinJobs;

	public bool CanStart(Guid providerId) =>
		providerId == Guid.Empty
		|| _running.GetValueOrDefault(providerId) < LimitOf(providerId);

	public void Take(Guid providerId)
	{
		if (providerId != Guid.Empty)
		{
			_running[providerId] = _running.GetValueOrDefault(providerId) + 1;
		}
	}
}
