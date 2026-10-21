namespace VibeSwarm.Shared.Models;

/// <summary>
/// The newest output lines of a job's current run, read from the process running it. Lines are
/// numbered in the order the provider wrote them, so a job page can tell which lines it already
/// has and fetch the ones it missed while it was loading or disconnected.
/// </summary>
public sealed class JobLiveOutput
{
	/// <summary>Lines kept for job pages; older ones fall off. Every page shows the same window.</summary>
	public const int MaxLines = 500;

	/// <summary>The number of the last line the run has written, 0 before its first.</summary>
	public long LatestSequence { get; set; }

	public List<JobOutputLine> Lines { get; set; } = [];
}

/// <summary>One line of a running job's output, numbered from 1 for each run.</summary>
public sealed class JobOutputLine
{
	public long Sequence { get; set; }
	public string Content { get; set; } = string.Empty;
	public bool IsError { get; set; }

	/// <summary>When the server received the line, so every device orders and times it the same.</summary>
	public DateTime Timestamp { get; set; }
}
