namespace VibeSwarm.Web.Models;

/// <summary>
/// Model for live output lines in job sessions
/// </summary>
public class OutputLine
{
	public string Content { get; set; } = string.Empty;
	public bool IsError { get; set; }
	public DateTime Timestamp { get; set; }
}
