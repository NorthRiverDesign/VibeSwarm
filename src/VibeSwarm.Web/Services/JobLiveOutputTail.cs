using VibeSwarm.Shared.Models;

namespace VibeSwarm.Web.Services;

/// <summary>
/// The newest output lines of a running job, numbered in the order they were written. Job pages
/// opened at different times, or catching up after a dropped connection, read the same lines from
/// here, so every device shows the same transcript.
/// </summary>
internal sealed class JobLiveOutputTail
{
	private readonly object _lock = new();
	private readonly Queue<JobOutputLine> _lines = new();
	private readonly int _maxLines;
	private long _latestSequence;
	private Task _broadcasts = Task.CompletedTask;

	public JobLiveOutputTail(int maxLines = JobLiveOutput.MaxLines)
	{
		_maxLines = maxLines;
	}

	/// <summary>Completes once every queued broadcast has been sent.</summary>
	public Task Broadcasts
	{
		get
		{
			lock (_lock)
			{
				return _broadcasts;
			}
		}
	}

	/// <summary>
	/// Numbers and keeps a line, then queues <paramref name="broadcast"/> behind the previous
	/// line's, so live pages receive lines in the order they were written.
	/// </summary>
	public JobOutputLine Append(string content, bool isError, Func<JobOutputLine, Task>? broadcast = null)
	{
		lock (_lock)
		{
			var line = new JobOutputLine
			{
				Sequence = ++_latestSequence,
				Content = content,
				IsError = isError,
				Timestamp = DateTime.UtcNow
			};

			_lines.Enqueue(line);
			while (_lines.Count > _maxLines)
			{
				_lines.Dequeue();
			}

			if (broadcast != null)
			{
				_broadcasts = _broadcasts.ContinueWith(async _ =>
				{
					try
					{
						await broadcast(line);
					}
					catch
					{
					}
				}, TaskScheduler.Default).Unwrap();
			}

			return line;
		}
	}

	/// <summary>
	/// The kept lines after <paramref name="afterSequence"/>. A number past the latest line came
	/// from another run's output (the job restarted), so the asker gets every kept line instead.
	/// </summary>
	public JobLiveOutput GetSince(long afterSequence)
	{
		lock (_lock)
		{
			var from = afterSequence > _latestSequence ? 0 : afterSequence;
			return new JobLiveOutput
			{
				LatestSequence = _latestSequence,
				Lines = _lines.Where(line => line.Sequence > from).ToList()
			};
		}
	}
}
