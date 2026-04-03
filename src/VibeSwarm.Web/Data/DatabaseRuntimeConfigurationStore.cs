using System.Text.Json;

namespace VibeSwarm.Shared.Data;

public interface IDatabaseRuntimeConfigurationStore
{
	string ConfigurationPath { get; }
	DatabaseRuntimeConfiguration? Load();
	Task SaveAsync(DatabaseRuntimeConfiguration configuration, CancellationToken cancellationToken = default);
}

public sealed class DatabaseRuntimeConfigurationStore : IDatabaseRuntimeConfigurationStore
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true
	};

	public DatabaseRuntimeConfigurationStore(string? configurationPath = null)
	{
		ConfigurationPath = configurationPath ?? GetDefaultPath();
	}

	public string ConfigurationPath { get; }

	public DatabaseRuntimeConfiguration? Load()
	{
		if (!File.Exists(ConfigurationPath))
		{
			return null;
		}

		var json = File.ReadAllText(ConfigurationPath);
		if (string.IsNullOrWhiteSpace(json))
		{
			return null;
		}

		var payload = JsonSerializer.Deserialize<DatabaseRuntimeConfigurationFile>(json, JsonOptions);
		if (payload == null)
		{
			return null;
		}

		return new DatabaseRuntimeConfiguration
		{
			Provider = payload.Database?.Provider,
			ConnectionString = payload.ConnectionStrings?.Default
		};
	}

	public async Task SaveAsync(DatabaseRuntimeConfiguration configuration, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(configuration);

		var directory = Path.GetDirectoryName(ConfigurationPath);
		if (string.IsNullOrWhiteSpace(directory))
		{
			throw new InvalidOperationException("Runtime database configuration path does not include a directory.");
		}

		Directory.CreateDirectory(directory);

		var payload = new DatabaseRuntimeConfigurationFile
		{
			Database = new DatabaseSection
			{
				Provider = configuration.Provider
			},
			ConnectionStrings = new ConnectionStringsSection
			{
				Default = configuration.ConnectionString
			}
		};

		var json = JsonSerializer.Serialize(payload, JsonOptions);
		var tempPath = $"{ConfigurationPath}.{Guid.NewGuid():N}.tmp";
		await File.WriteAllTextAsync(tempPath, json, cancellationToken);

		File.Move(tempPath, ConfigurationPath, true);
	}

	public static string GetDefaultPath()
	{
		var directory = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"VibeSwarm");
		return Path.Combine(directory, "database.runtime.json");
	}

	private sealed class DatabaseRuntimeConfigurationFile
	{
		public DatabaseSection? Database { get; set; }
		public ConnectionStringsSection? ConnectionStrings { get; set; }
	}

	private sealed class DatabaseSection
	{
		public string? Provider { get; set; }
	}

	private sealed class ConnectionStringsSection
	{
		public string? Default { get; set; }
	}
}

public sealed class DatabaseRuntimeConfiguration
{
	public string? Provider { get; set; }
	public string? ConnectionString { get; set; }
}
