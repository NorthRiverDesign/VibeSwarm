namespace VibeSwarm.Shared.Models;

public class DatabaseConfigurationInfo
{
	public string Provider { get; set; } = "unknown";
	public string? ConnectionStringPreview { get; set; }
	public string ConfigurationSource { get; set; } = string.Empty;
	public string RuntimeConfigurationPath { get; set; } = string.Empty;
	public bool HasEnvironmentOverride { get; set; }
	public bool CanUpdateConfiguration { get; set; }
	public string? PendingProvider { get; set; }
	public string? PendingConnectionStringPreview { get; set; }
}

public class DatabaseMigrationRequest
{
	public string Provider { get; set; } = "sqlite";
	public string ConnectionString { get; set; } = string.Empty;
}

public class DatabaseMigrationResult
{
	public string Provider { get; set; } = string.Empty;
	public string? ConnectionStringPreview { get; set; }
	public int CopiedTableCount { get; set; }
	public int CopiedRowCount { get; set; }
	public bool RestartRequired { get; set; } = true;
	public string Message { get; set; } = string.Empty;
}
