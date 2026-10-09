using System.Diagnostics;

namespace VibeSwarm.Web.Services;

/// <summary>
/// A job's running-time limit, the guardrail against a run that keeps spending usage. Only time
/// the agent is working counts: waiting for an answer to a question spends nothing, so the clock
/// pauses. When the limit is reached, <c>onReached</c> runs once (the job's run is cancelled,
/// which stops the agent's process).
/// </summary>
public sealed class JobTimeLimit : IDisposable
{
	private const string StopMessagePrefix = "Stopped at its time limit";

	private readonly CancellationTokenSource _timer = new();
	private readonly Stopwatch _running = new();
	private readonly Lock _gate = new();
	private bool _stopped;

	public JobTimeLimit(TimeSpan limit, Action onReached)
	{
		Limit = limit;
		_timer.Token.Register(() =>
		{
			Reached = true;
			onReached();
		});
		Resume();
	}

	public TimeSpan Limit { get; }

	public bool Reached { get; private set; }

	/// <summary>Stops the clock while the job waits for the user.</summary>
	public void Pause()
	{
		lock (_gate)
		{
			if (_stopped || Reached || !_running.IsRunning)
			{
				return;
			}

			_running.Stop();
			_timer.CancelAfter(Timeout.InfiniteTimeSpan);
		}
	}

	public void Resume()
	{
		lock (_gate)
		{
			if (_stopped || Reached || _running.IsRunning)
			{
				return;
			}

			_running.Start();
			var remaining = Limit - _running.Elapsed;
			_timer.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
		}
	}

	/// <summary>
	/// The agent has finished: build checks and delivery that follow must not trip the limit.
	/// </summary>
	public void Stop()
	{
		lock (_gate)
		{
			if (_stopped)
			{
				return;
			}

			_stopped = true;
			_running.Stop();
			if (!Reached)
			{
				_timer.CancelAfter(Timeout.InfiniteTimeSpan);
			}
		}
	}

	public static string BuildStopMessage(TimeSpan limit, string? keptOn) =>
		$"{StopMessagePrefix} of {FormatLimit(limit)}, before the agent finished. " +
		(keptOn != null
			? $"Its work so far is kept on the branch {keptOn}, and a follow-up picks it up. "
			: string.Empty) +
		"Raise the limit or split the task before running it again.";

	public static bool IsStopMessage(string? errorMessage) =>
		errorMessage?.StartsWith(StopMessagePrefix, StringComparison.Ordinal) == true;

	private static string FormatLimit(TimeSpan limit) => limit.TotalMinutes >= 1
		? $"{limit.TotalMinutes:0} minute{(limit.TotalMinutes >= 2 ? "s" : string.Empty)}"
		: $"{limit.TotalSeconds:0} seconds";

	public void Dispose()
	{
		lock (_gate)
		{
			_stopped = true;
			_timer.Dispose();
		}
	}
}
