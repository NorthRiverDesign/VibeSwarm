using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace VibeSwarm.Shared.Services;

/// <summary>
/// Tracks provider health with circuit breaker pattern to prevent cascading failures.
/// Monitors success/failure rates, response times, and availability.
/// </summary>
public class ProviderHealthTracker : IProviderHealthTracker
{
	private readonly ConcurrentDictionary<Guid, ProviderHealthState> _healthStates = new();
	private readonly ILogger<ProviderHealthTracker>? _logger;
	private readonly object _statsLock = new();

	/// <summary>
	/// Number of failures before opening the circuit breaker
	/// </summary>
	public int FailureThreshold { get; set; } = 5;

	/// <summary>
	/// Time window for counting failures
	/// </summary>
	public TimeSpan FailureWindow { get; set; } = TimeSpan.FromMinutes(5);

	/// <summary>
	/// How long to wait before testing a tripped circuit
	/// </summary>
	public TimeSpan CircuitResetTimeout { get; set; } = TimeSpan.FromMinutes(2);

	/// <summary>
	/// How long to wait before testing a circuit tripped by a system-level failure
	/// (upstream outage, model unavailable). Longer than normal to avoid hammering a down provider.
	/// </summary>
	public TimeSpan SystemFailureResetTimeout { get; set; } = TimeSpan.FromMinutes(5);

	/// <summary>
	/// Default cooldown when a rate limit is detected but no reset time is provided.
	/// </summary>
	public TimeSpan DefaultRateLimitCooldown { get; set; } = TimeSpan.FromMinutes(1);

	/// <summary>
	/// Consecutive rate-limit backoff steps. The last value is reused once the sequence is exhausted.
	/// </summary>
	public TimeSpan[] RateLimitCooldownSteps { get; set; } =
	[
		TimeSpan.FromMinutes(1),
		TimeSpan.FromMinutes(2),
		TimeSpan.FromMinutes(3)
	];

	/// <summary>
	/// Number of successes needed to close a half-open circuit
	/// </summary>
	public int SuccessThreshold { get; set; } = 2;

	public ProviderHealthTracker(ILogger<ProviderHealthTracker>? logger = null)
	{
		_logger = logger;
	}

	/// <summary>
	/// Gets or creates health state for a provider
	/// </summary>
	public ProviderHealth GetProviderHealth(Guid providerId)
	{
		var state = _healthStates.GetOrAdd(providerId, _ => new ProviderHealthState());

		lock (state.Lock)
		{
			// Clean up old events
			var cutoff = DateTime.UtcNow - FailureWindow;
			state.RecentFailures.RemoveAll(f => f < cutoff);
			state.RecentSuccesses.RemoveAll(s => s < cutoff);
			state.ResponseTimes.RemoveAll(r => r.Timestamp < cutoff);

			// Check circuit breaker state
			var circuitState = EvaluateCircuitState(state);

			return new ProviderHealth
			{
				ProviderId = providerId,
				IsHealthy = circuitState != CircuitState.Open,
				CircuitState = circuitState,
				CurrentLoad = state.CurrentLoad,
				TotalSuccesses = state.TotalSuccesses,
				TotalFailures = state.TotalFailures,
				RecentFailureRate = CalculateFailureRate(state),
				AverageResponseTime = CalculateAverageResponseTime(state),
				LastSuccess = state.LastSuccess,
				LastFailure = state.LastFailure,
				LastError = state.LastError,
				IsRateLimited = state.IsRateLimited,
				RateLimitResetTime = state.RateLimitResetTime
			};
		}
	}

	/// <summary>
	/// Records a successful operation
	/// </summary>
	public void RecordSuccess(Guid providerId, TimeSpan? responseTime = null)
	{
		var state = _healthStates.GetOrAdd(providerId, _ => new ProviderHealthState());
		var now = DateTime.UtcNow;

		lock (state.Lock)
		{
			state.TotalSuccesses++;
			state.RecentSuccesses.Add(now);
			state.LastSuccess = now;
			state.ConsecutiveSuccesses++;
			state.ConsecutiveFailures = 0;

			if (responseTime.HasValue)
			{
				state.ResponseTimes.Add(new ResponseTimeEntry { Timestamp = now, Duration = responseTime.Value });
			}

			if (state.IsRateLimited || state.ConsecutiveRateLimitFailures > 0)
			{
				state.IsRateLimited = false;
				state.RateLimitResetTime = null;
				state.ConsecutiveRateLimitFailures = 0;
			}

			// Check if we should close a half-open circuit
			if (state.CircuitState == CircuitState.HalfOpen &&
				state.ConsecutiveSuccesses >= SuccessThreshold)
			{
				state.CircuitState = CircuitState.Closed;
				state.IsSystemFailure = false;
				state.IsRateLimited = false;
				state.RateLimitResetTime = null;
				_logger?.LogInformation("Circuit breaker closed for provider {ProviderId} after {Successes} consecutive successes",
					providerId, state.ConsecutiveSuccesses);
			}
		}
	}

	/// <summary>
	/// Records a failed operation
	/// </summary>
	public void RecordFailure(Guid providerId, string? errorMessage = null, TimeSpan? responseTime = null)
	{
		var state = _healthStates.GetOrAdd(providerId, _ => new ProviderHealthState());
		var now = DateTime.UtcNow;

		lock (state.Lock)
		{
			state.TotalFailures++;
			state.RecentFailures.Add(now);
			state.LastFailure = now;
			state.LastError = errorMessage;
			state.ConsecutiveFailures++;
			state.ConsecutiveSuccesses = 0;

			if (responseTime.HasValue)
			{
				state.ResponseTimes.Add(new ResponseTimeEntry { Timestamp = now, Duration = responseTime.Value });
			}

			// Check if we should trip the circuit breaker
			if (state.CircuitState == CircuitState.Closed &&
				state.RecentFailures.Count >= FailureThreshold)
			{
				state.CircuitState = CircuitState.Open;
				state.CircuitOpenedAt = now;
				_logger?.LogWarning("Circuit breaker opened for provider {ProviderId} after {Failures} failures: {Error}",
					providerId, state.RecentFailures.Count, errorMessage);
			}
			else if (state.CircuitState == CircuitState.HalfOpen)
			{
				// Failed while testing - reopen circuit
				state.CircuitState = CircuitState.Open;
				state.CircuitOpenedAt = now;
				_logger?.LogWarning("Circuit breaker re-opened for provider {ProviderId} after test failure: {Error}",
					providerId, errorMessage);
			}
		}
	}

	/// <summary>
	/// Records a system-level failure (model unavailable, upstream outage, auth failure).
	/// Immediately opens the circuit breaker regardless of failure threshold, since
	/// system errors indicate the provider is globally unavailable.
	/// </summary>
	public void RecordSystemFailure(Guid providerId, string? errorMessage = null)
	{
		var state = _healthStates.GetOrAdd(providerId, _ => new ProviderHealthState());
		var now = DateTime.UtcNow;

		lock (state.Lock)
		{
			state.TotalFailures++;
			state.RecentFailures.Add(now);
			state.LastFailure = now;
			state.LastError = errorMessage;
			state.ConsecutiveFailures++;
			state.ConsecutiveSuccesses = 0;

			// Immediately open the circuit regardless of threshold
			state.CircuitState = CircuitState.Open;
			state.CircuitOpenedAt = now;
			state.IsSystemFailure = true;

			_logger?.LogWarning(
				"Circuit breaker immediately opened for provider {ProviderId} due to system error: {Error}. " +
				"Provider will be retested after {ResetTimeout}",
				providerId, errorMessage, SystemFailureResetTimeout);
		}
	}

	/// <summary>
	/// Records a rate limit failure with an optional reset time.
	/// Opens the circuit until the reset time (which may be hours away for providers like GitHub Copilot).
	/// </summary>
	public void RecordRateLimitFailure(Guid providerId, string? errorMessage = null, DateTime? resetTime = null)
	{
		var state = _healthStates.GetOrAdd(providerId, _ => new ProviderHealthState());
		var now = DateTime.UtcNow;

		lock (state.Lock)
		{
			state.TotalFailures++;
			state.RecentFailures.Add(now);
			state.LastFailure = now;
			state.LastError = errorMessage;
			state.ConsecutiveFailures++;
			state.ConsecutiveRateLimitFailures = Math.Min(
				state.ConsecutiveRateLimitFailures + 1,
				Math.Max(RateLimitCooldownSteps.Length, 1));
			state.ConsecutiveSuccesses = 0;

			var scheduledResetTime = now + GetRateLimitCooldown(state.ConsecutiveRateLimitFailures);
			var effectiveResetTime = scheduledResetTime;
			if (resetTime.HasValue && resetTime.Value > effectiveResetTime)
			{
				effectiveResetTime = resetTime.Value;
			}

			state.CircuitState = CircuitState.Open;
			state.CircuitOpenedAt = now;
			state.IsSystemFailure = false;
			state.IsRateLimited = true;
			state.RateLimitResetTime = effectiveResetTime;

			_logger?.LogWarning(
				"Circuit breaker opened for provider {ProviderId} due to rate limit: {Error}. " +
				"Provider will be retested after {ResetTime:u} (consecutive rate limits: {RateLimitCount})",
				providerId, errorMessage, state.RateLimitResetTime, state.ConsecutiveRateLimitFailures);
		}
	}

	/// <summary>
	/// Increments the current load for a provider
	/// </summary>
	public void IncrementProviderLoad(Guid providerId)
	{
		var state = _healthStates.GetOrAdd(providerId, _ => new ProviderHealthState());
		Interlocked.Increment(ref state.CurrentLoad);
	}

	/// <summary>
	/// Decrements the current load for a provider
	/// </summary>
	public void DecrementProviderLoad(Guid providerId)
	{
		if (_healthStates.TryGetValue(providerId, out var state))
		{
			var newValue = Interlocked.Decrement(ref state.CurrentLoad);
			if (newValue < 0)
			{
				Interlocked.Exchange(ref state.CurrentLoad, 0);
			}
		}
	}

	/// <summary>
	/// Resets health tracking for a provider
	/// </summary>
	public void ResetProvider(Guid providerId)
	{
		_healthStates.TryRemove(providerId, out _);
		_logger?.LogInformation("Reset health tracking for provider {ProviderId}", providerId);
	}

	/// <summary>
	/// Forces a circuit breaker state
	/// </summary>
	public void ForceCircuitState(Guid providerId, CircuitState state)
	{
		var healthState = _healthStates.GetOrAdd(providerId, _ => new ProviderHealthState());
		lock (healthState.Lock)
		{
			healthState.CircuitState = state;
			if (state == CircuitState.Open)
			{
				healthState.CircuitOpenedAt = DateTime.UtcNow;
			}
			if (state == CircuitState.Closed)
			{
				healthState.IsRateLimited = false;
				healthState.RateLimitResetTime = null;
				healthState.ConsecutiveRateLimitFailures = 0;
			}
		}
		_logger?.LogInformation("Forced circuit state to {State} for provider {ProviderId}", state, providerId);
	}

	/// <summary>
	/// Gets summary of all provider health states
	/// </summary>
	public IReadOnlyDictionary<Guid, ProviderHealth> GetAllProviderHealth()
	{
		return _healthStates.Keys.ToDictionary(id => id, id => GetProviderHealth(id));
	}

	private CircuitState EvaluateCircuitState(ProviderHealthState state)
	{
		if (state.CircuitState == CircuitState.Open)
		{
			if (state.IsRateLimited && state.RateLimitResetTime.HasValue)
			{
				// Rate-limited: wait until the provider's reset time
				if (DateTime.UtcNow >= state.RateLimitResetTime.Value)
				{
					state.CircuitState = CircuitState.HalfOpen;
					state.IsRateLimited = false;
					state.RateLimitResetTime = null;
					_logger?.LogInformation("Circuit breaker moved to half-open after rate limit cooldown expired");
				}
			}
			else
			{
				// Use longer timeout for system-level failures
				var resetTimeout = state.IsSystemFailure ? SystemFailureResetTimeout : CircuitResetTimeout;

				// Check if we should move to half-open
				if (state.CircuitOpenedAt.HasValue &&
					DateTime.UtcNow - state.CircuitOpenedAt.Value >= resetTimeout)
				{
					state.CircuitState = CircuitState.HalfOpen;
					_logger?.LogInformation("Circuit breaker moved to half-open state after timeout");
				}
			}
		}

		return state.CircuitState;
	}

	private double CalculateFailureRate(ProviderHealthState state)
	{
		var total = state.RecentFailures.Count + state.RecentSuccesses.Count;
		if (total == 0) return 0;
		return (double)state.RecentFailures.Count / total;
	}

	private TimeSpan GetRateLimitCooldown(int consecutiveRateLimitFailures)
	{
		if (RateLimitCooldownSteps.Length == 0)
		{
			return DefaultRateLimitCooldown;
		}

		var stepIndex = Math.Clamp(consecutiveRateLimitFailures - 1, 0, RateLimitCooldownSteps.Length - 1);
		return RateLimitCooldownSteps[stepIndex];
	}

	private TimeSpan CalculateAverageResponseTime(ProviderHealthState state)
	{
		if (state.ResponseTimes.Count == 0)
			return TimeSpan.Zero;

		var avgMs = state.ResponseTimes.Average(r => r.Duration.TotalMilliseconds);
		return TimeSpan.FromMilliseconds(avgMs);
	}

	/// <summary>
	/// Internal state for tracking provider health
	/// </summary>
	private class ProviderHealthState
	{
		public readonly object Lock = new();
		public int CurrentLoad;
		public long TotalSuccesses;
		public long TotalFailures;
		public int ConsecutiveSuccesses;
		public int ConsecutiveFailures;
		public List<DateTime> RecentFailures { get; } = new();
		public List<DateTime> RecentSuccesses { get; } = new();
		public List<ResponseTimeEntry> ResponseTimes { get; } = new();
		public DateTime? LastSuccess;
		public DateTime? LastFailure;
		public string? LastError;
		public CircuitState CircuitState = CircuitState.Closed;
		public DateTime? CircuitOpenedAt;
		public bool IsSystemFailure;
		public bool IsRateLimited;
		public DateTime? RateLimitResetTime;
		public int ConsecutiveRateLimitFailures;
	}

	private class ResponseTimeEntry
	{
		public DateTime Timestamp { get; init; }
		public TimeSpan Duration { get; init; }
	}
}

/// <summary>
/// Health information for a provider
/// </summary>
public class ProviderHealth
{
	public Guid ProviderId { get; set; }
	public bool IsHealthy { get; set; }
	public CircuitState CircuitState { get; set; }
	public int CurrentLoad { get; set; }
	public long TotalSuccesses { get; set; }
	public long TotalFailures { get; set; }
	public double RecentFailureRate { get; set; }
	public TimeSpan AverageResponseTime { get; set; }
	public DateTime? LastSuccess { get; set; }
	public DateTime? LastFailure { get; set; }
	public string? LastError { get; set; }
	public bool IsRateLimited { get; set; }
	public DateTime? RateLimitResetTime { get; set; }
}

/// <summary>
/// Circuit breaker states
/// </summary>
public enum CircuitState
{
	/// <summary>
	/// Normal operation, requests are allowed
	/// </summary>
	Closed,

	/// <summary>
	/// Circuit is tripped, requests are blocked
	/// </summary>
	Open,

	/// <summary>
	/// Testing if the provider has recovered
	/// </summary>
	HalfOpen
}

/// <summary>
/// Interface for provider health tracking
/// </summary>
public interface IProviderHealthTracker
{
	int FailureThreshold { get; set; }
	TimeSpan FailureWindow { get; set; }
	TimeSpan CircuitResetTimeout { get; set; }

	ProviderHealth GetProviderHealth(Guid providerId);
	void RecordSuccess(Guid providerId, TimeSpan? responseTime = null);
	void RecordFailure(Guid providerId, string? errorMessage = null, TimeSpan? responseTime = null);
	void RecordSystemFailure(Guid providerId, string? errorMessage = null);
	void RecordRateLimitFailure(Guid providerId, string? errorMessage = null, DateTime? resetTime = null);
	void IncrementProviderLoad(Guid providerId);
	void DecrementProviderLoad(Guid providerId);
	void ResetProvider(Guid providerId);
	void ForceCircuitState(Guid providerId, CircuitState state);
	IReadOnlyDictionary<Guid, ProviderHealth> GetAllProviderHealth();
}
