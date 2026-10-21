using VibeSwarm.Client.Models;
using VibeSwarm.Shared.Models;

namespace VibeSwarm.Client.Components.Jobs;

/// <summary>
/// The job page's copy of a running job's output. Live lines arrive numbered from the server; the
/// buffer keeps them in order, drops repeats, and holds back lines that arrive after a gap until the
/// page has fetched the missing ones, so every device shows the same transcript.
/// </summary>
public sealed class JobLiveOutputBuffer
{
	private readonly object _lock = new();
	private readonly List<OutputLine> _lines = new();
	// Lines that arrived ahead of missing ones, shown once the gap is filled
	private readonly SortedDictionary<long, JobOutputLine> _waiting = new();
	private readonly int _maxLines;
	private long _lastSequence;
	private bool _isSynced;
	private int _generation;

	public JobLiveOutputBuffer(int maxLines = JobLiveOutput.MaxLines)
	{
		_maxLines = maxLines;
	}

	/// <summary>The number of the newest line shown, 0 when none came numbered.</summary>
	public long LastSequence
	{
		get { lock (_lock) { return _lastSequence; } }
	}

	/// <summary>True once the server's copy of the run has been loaded.</summary>
	public bool IsSynced
	{
		get { lock (_lock) { return _isSynced; } }
	}

	public bool IsEmpty
	{
		get { lock (_lock) { return _lines.Count == 0; } }
	}

	public List<OutputLine> ToList()
	{
		lock (_lock)
		{
			return new List<OutputLine>(_lines);
		}
	}

	/// <summary>Hides the lines shown so far. Later lines keep coming; cleared ones don't return.</summary>
	public void Clear()
	{
		lock (_lock)
		{
			_lines.Clear();
		}
	}

	/// <summary>Forgets the output entirely, for another job or a new run of this one.</summary>
	public void Reset()
	{
		lock (_lock)
		{
			ResetCore();
		}
	}

	/// <summary>
	/// Shows saved output, which has no line numbers, when the server has no live copy of the run
	/// (the job has finished, or isn't running in this server).
	/// </summary>
	public void LoadSavedOutput(string consoleOutput)
	{
		lock (_lock)
		{
			_lines.Clear();
			var timestamp = DateTime.UtcNow;
			foreach (var line in consoleOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(_maxLines))
			{
				_lines.Add(JobSessionDisplayBuilder.CreateOutputLine(line, timestamp));
			}
		}
	}

	/// <summary>
	/// Adds a line pushed live. Returns false when earlier lines are missing: the line waits, and the
	/// page should fetch the gap (<see cref="BeginCatchUp"/>, then <see cref="ApplyCatchUp"/>).
	/// </summary>
	public bool Add(JobOutputLine line)
	{
		lock (_lock)
		{
			// Line 1 while holding numbered lines: the job started a new run (a retry, or a restart)
			if (line.Sequence == 1 && _lastSequence > 0)
			{
				ResetCore();
				_isSynced = true;
			}

			if (line.Sequence <= _lastSequence)
			{
				return true;
			}

			if (line.Sequence == _lastSequence + 1 && (_isSynced || line.Sequence == 1))
			{
				_isSynced = true;
				AppendLine(line);
				DrainWaiting();
				return _waiting.Count == 0;
			}

			_waiting[line.Sequence] = line;
			while (_waiting.Count > _maxLines)
			{
				_waiting.Remove(_waiting.Keys.First());
			}

			return false;
		}
	}

	/// <summary>The line number to fetch after, and a token that tells a stale reply apart.</summary>
	public (long AfterSequence, int Generation) BeginCatchUp()
	{
		lock (_lock)
		{
			return (_isSynced ? _lastSequence : 0, _generation);
		}
	}

	/// <summary>
	/// Applies the server's lines after <paramref name="afterSequence"/>. A fetch from 0 replaces what
	/// is shown, since the server holds the run's output. Null means the server has no live copy, so
	/// the lines that arrived are shown as they are. Returns false while lines are still missing.
	/// </summary>
	public bool ApplyCatchUp(JobLiveOutput? output, long afterSequence, int generation)
	{
		lock (_lock)
		{
			if (generation != _generation)
			{
				// The output was reset (a new run) while the fetch was out: the reply is the old run's
				return _waiting.Count == 0;
			}

			if (output == null)
			{
				if (_waiting.Count > 0)
				{
					foreach (var line in _waiting.Values.Where(line => line.Sequence > _lastSequence))
					{
						AppendLine(line);
					}

					_waiting.Clear();
					_isSynced = true;
				}

				return true;
			}

			// From 0 the server's copy replaces saved output, unless lines that arrived during the fetch
			// already go past it. A server with fewer lines than asked for has begun a new run.
			var startOver = afterSequence == 0
				? _lastSequence <= output.LatestSequence
				: output.LatestSequence < afterSequence;
			if (startOver)
			{
				_lines.Clear();
				_lastSequence = 0;
			}

			foreach (var line in output.Lines.OrderBy(line => line.Sequence))
			{
				if (line.Sequence > _lastSequence)
				{
					AppendLine(line);
				}
			}

			_isSynced = true;
			DrainWaiting();
			return _waiting.Count == 0;
		}
	}

	private void ResetCore()
	{
		_lines.Clear();
		_waiting.Clear();
		_lastSequence = 0;
		_isSynced = false;
		_generation++;
	}

	private void DrainWaiting()
	{
		while (_waiting.Count > 0)
		{
			var next = _waiting.First();
			if (next.Key <= _lastSequence)
			{
				_waiting.Remove(next.Key);
			}
			else if (next.Key == _lastSequence + 1)
			{
				_waiting.Remove(next.Key);
				AppendLine(next.Value);
			}
			else
			{
				return;
			}
		}
	}

	private void AppendLine(JobOutputLine line)
	{
		_lines.Add(JobSessionDisplayBuilder.CreateOutputLine(
			line.IsError ? $"[ERR] {line.Content}" : line.Content,
			line.Timestamp));
		_lastSequence = line.Sequence;

		// Trim from the front in one pass to avoid repeated O(n) RemoveAt(0) calls.
		if (_lines.Count > _maxLines)
		{
			_lines.RemoveRange(0, _lines.Count - _maxLines);
		}
	}
}
