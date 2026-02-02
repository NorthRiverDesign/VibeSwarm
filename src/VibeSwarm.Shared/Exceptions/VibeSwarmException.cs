namespace VibeSwarm.Shared.Exceptions;

/// <summary>
/// Base exception for all VibeSwarm application errors
/// </summary>
public class VibeSwarmException : Exception
{
	public string ErrorCode { get; }
	public bool IsRecoverable { get; }

	public VibeSwarmException(string message, string errorCode = "VIBESWARM_ERROR", bool isRecoverable = true)
		: base(message)
	{
		ErrorCode = errorCode;
		IsRecoverable = isRecoverable;
	}

	public VibeSwarmException(string message, Exception innerException, string errorCode = "VIBESWARM_ERROR", bool isRecoverable = true)
		: base(message, innerException)
	{
		ErrorCode = errorCode;
		IsRecoverable = isRecoverable;
	}
}

/// <summary>
/// Exception thrown when a Git operation fails
/// </summary>
public class GitException : VibeSwarmException
{
	public string? WorkingDirectory { get; }
	public string? GitCommand { get; }

	public GitException(string message, string? workingDirectory = null, string? gitCommand = null)
		: base(message, "GIT_ERROR", true)
	{
		WorkingDirectory = workingDirectory;
		GitCommand = gitCommand;
	}

	public GitException(string message, Exception innerException, string? workingDirectory = null, string? gitCommand = null)
		: base(message, innerException, "GIT_ERROR", true)
	{
		WorkingDirectory = workingDirectory;
		GitCommand = gitCommand;
	}
}

/// <summary>
/// Exception thrown when Git is not available on the system
/// </summary>
public class GitNotAvailableException : GitException
{
	public GitNotAvailableException()
		: base("Git is not installed or not available in the system PATH.", null, null)
	{
	}
}

/// <summary>
/// Exception thrown when a path is not a Git repository
/// </summary>
public class NotAGitRepositoryException : GitException
{
	public NotAGitRepositoryException(string workingDirectory)
		: base($"The path '{workingDirectory}' is not a Git repository.", workingDirectory, null)
	{
	}
}

/// <summary>
/// Exception thrown when a CLI agent operation fails
/// </summary>
public class CliAgentException : VibeSwarmException
{
	public string ProviderName { get; }
	public string? Command { get; }
	public int? ExitCode { get; }

	public CliAgentException(string message, string providerName, string? command = null, int? exitCode = null)
		: base(message, "CLI_AGENT_ERROR", true)
	{
		ProviderName = providerName;
		Command = command;
		ExitCode = exitCode;
	}

	public CliAgentException(string message, Exception innerException, string providerName, string? command = null, int? exitCode = null)
		: base(message, innerException, "CLI_AGENT_ERROR", true)
	{
		ProviderName = providerName;
		Command = command;
		ExitCode = exitCode;
	}
}

/// <summary>
/// Exception thrown when a CLI agent is not available or not configured
/// </summary>
public class CliAgentNotAvailableException : CliAgentException
{
	public CliAgentNotAvailableException(string providerName)
		: base($"The CLI agent '{providerName}' is not available. Please ensure it is installed and configured correctly.", providerName)
	{
	}
}

/// <summary>
/// Exception thrown when a CLI agent requires authentication
/// </summary>
public class CliAgentAuthenticationException : CliAgentException
{
	public CliAgentAuthenticationException(string providerName)
		: base($"The CLI agent '{providerName}' requires authentication. Please run the agent manually to complete authentication.", providerName)
	{
	}
}

/// <summary>
/// Exception thrown when a CLI agent exceeds its usage limit
/// </summary>
public class CliAgentUsageLimitException : CliAgentException
{
	public decimal? UsagePercentage { get; }
	public DateTime? ResetTime { get; }

	public CliAgentUsageLimitException(string providerName, decimal? usagePercentage = null, DateTime? resetTime = null)
		: base($"The CLI agent '{providerName}' has exceeded its usage limit.", providerName)
	{
		UsagePercentage = usagePercentage;
		ResetTime = resetTime;
	}
}

/// <summary>
/// Exception thrown when a filesystem operation fails
/// </summary>
public class FileSystemException : VibeSwarmException
{
	public string? Path { get; }
	public FileSystemOperation Operation { get; }

	public FileSystemException(string message, string? path = null, FileSystemOperation operation = FileSystemOperation.Unknown)
		: base(message, "FILESYSTEM_ERROR", true)
	{
		Path = path;
		Operation = operation;
	}

	public FileSystemException(string message, Exception innerException, string? path = null, FileSystemOperation operation = FileSystemOperation.Unknown)
		: base(message, innerException, "FILESYSTEM_ERROR", true)
	{
		Path = path;
		Operation = operation;
	}
}

/// <summary>
/// Exception thrown when a path is not found
/// </summary>
public class PathNotFoundException : FileSystemException
{
	public PathNotFoundException(string path)
		: base($"The path '{path}' was not found.", path, FileSystemOperation.Read)
	{
	}
}

/// <summary>
/// Exception thrown when access to a path is denied
/// </summary>
public class PathAccessDeniedException : FileSystemException
{
	public PathAccessDeniedException(string path, FileSystemOperation operation = FileSystemOperation.Unknown)
		: base($"Access to the path '{path}' was denied.", path, operation)
	{
	}
}

/// <summary>
/// Exception thrown when an entity is not found
/// </summary>
public class EntityNotFoundException : VibeSwarmException
{
	public string EntityType { get; }
	public string? EntityId { get; }

	public EntityNotFoundException(string entityType, string? entityId = null)
		: base($"{entityType} {(entityId != null ? $"with ID '{entityId}' " : "")}was not found.", "ENTITY_NOT_FOUND", true)
	{
		EntityType = entityType;
		EntityId = entityId;
	}
}

/// <summary>
/// Exception thrown when an API request fails
/// </summary>
public class ApiException : VibeSwarmException
{
	public int StatusCode { get; }
	public string? ResponseContent { get; }

	public ApiException(string message, int statusCode, string? responseContent = null)
		: base(message, "API_ERROR", true)
	{
		StatusCode = statusCode;
		ResponseContent = responseContent;
	}

	public ApiException(string message, int statusCode, Exception innerException, string? responseContent = null)
		: base(message, innerException, "API_ERROR", true)
	{
		StatusCode = statusCode;
		ResponseContent = responseContent;
	}
}

/// <summary>
/// Exception thrown when JSON deserialization fails
/// </summary>
public class JsonParseException : VibeSwarmException
{
	public string? RawContent { get; }
	public Type? TargetType { get; }

	public JsonParseException(string message, string? rawContent = null, Type? targetType = null)
		: base(message, "JSON_PARSE_ERROR", true)
	{
		RawContent = rawContent;
		TargetType = targetType;
	}

	public JsonParseException(string message, Exception innerException, string? rawContent = null, Type? targetType = null)
		: base(message, innerException, "JSON_PARSE_ERROR", true)
	{
		RawContent = rawContent;
		TargetType = targetType;
	}
}

/// <summary>
/// Represents the type of filesystem operation
/// </summary>
public enum FileSystemOperation
{
	Unknown,
	Read,
	Write,
	Delete,
	Create,
	List,
	Move,
	Copy
}
