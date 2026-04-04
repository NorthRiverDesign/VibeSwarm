namespace VibeSwarm.Web.Services;

public class DeveloperModeOptions
{
	public const string SectionName = "DeveloperMode";

	public bool Enabled { get; set; }
	public string? BuildCommand { get; set; }
	public string? RestartCommand { get; set; }
	public string? ServiceName { get; set; }
	public string? WorkingDirectory { get; set; }
	public int RestartDelaySeconds { get; set; } = 2;
	public int MaxOutputLines { get; set; } = 200;
}
