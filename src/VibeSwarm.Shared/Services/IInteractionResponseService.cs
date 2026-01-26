using System.Collections.Concurrent;

namespace VibeSwarm.Shared.Services;

/// <summary>
/// Service for managing pending interaction responses across the application.
/// Allows the worker to wait for user responses and the UI to deliver them.
/// </summary>
public interface IInteractionResponseService
{
	/// <summary>
	/// Registers a pending response handler for a job and returns a task that completes when a response is received.
	/// </summary>
	/// <param name="jobId">The job ID waiting for interaction</param>
	/// <param name="timeout">Optional timeout for waiting</param>
	/// <param name="cancellationToken">Cancellation token</param>
	/// <returns>The user's response, or null if cancelled/timed out</returns>
	Task<string?> WaitForResponseAsync(Guid jobId, TimeSpan? timeout = null, CancellationToken cancellationToken = default);

	/// <summary>
	/// Submits a response for a waiting job.
	/// </summary>
	/// <param name="jobId">The job ID</param>
	/// <param name="response">The user's response</param>
	/// <returns>True if the response was delivered to a waiting handler</returns>
	bool SubmitResponse(Guid jobId, string response);

	/// <summary>
	/// Cancels a pending response wait.
	/// </summary>
	/// <param name="jobId">The job ID</param>
	void CancelWait(Guid jobId);

	/// <summary>
	/// Checks if there's a pending wait for a job.
	/// </summary>
	bool HasPendingWait(Guid jobId);
}

/// <summary>
/// In-memory implementation of IInteractionResponseService.
/// For distributed scenarios, consider using Redis or a database-backed implementation.
/// </summary>
public class InMemoryInteractionResponseService : IInteractionResponseService
{
	private readonly ConcurrentDictionary<Guid, TaskCompletionSource<string?>> _pendingResponses = new();

	public async Task<string?> WaitForResponseAsync(Guid jobId, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
	{
		var tcs = new TaskCompletionSource<string?>();

		// Register cancellation
		using var registration = cancellationToken.Register(() =>
		{
			tcs.TrySetCanceled();
			_pendingResponses.TryRemove(jobId, out _);
		});

		// Add or update the pending response
		_pendingResponses.AddOrUpdate(jobId, tcs, (_, __) => tcs);

		try
		{
			if (timeout.HasValue)
			{
				using var timeoutCts = new CancellationTokenSource(timeout.Value);
				using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

				var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, linkedCts.Token));

				if (completedTask == tcs.Task)
				{
					return await tcs.Task;
				}

				// Timeout or cancelled
				_pendingResponses.TryRemove(jobId, out _);
				return null;
			}
			else
			{
				return await tcs.Task;
			}
		}
		catch (OperationCanceledException)
		{
			_pendingResponses.TryRemove(jobId, out _);
			return null;
		}
		finally
		{
			_pendingResponses.TryRemove(jobId, out _);
		}
	}

	public bool SubmitResponse(Guid jobId, string response)
	{
		if (_pendingResponses.TryRemove(jobId, out var tcs))
		{
			return tcs.TrySetResult(response);
		}
		return false;
	}

	public void CancelWait(Guid jobId)
	{
		if (_pendingResponses.TryRemove(jobId, out var tcs))
		{
			tcs.TrySetCanceled();
		}
	}

	public bool HasPendingWait(Guid jobId)
	{
		return _pendingResponses.ContainsKey(jobId);
	}
}
