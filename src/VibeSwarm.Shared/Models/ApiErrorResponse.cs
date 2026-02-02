using System.Text.Json.Serialization;

namespace VibeSwarm.Shared.Models;

/// <summary>
/// Standardized API error response model
/// </summary>
public class ApiErrorResponse
{
	[JsonPropertyName("success")]
	public bool Success { get; set; } = false;

	[JsonPropertyName("errorCode")]
	public string ErrorCode { get; set; } = "UNKNOWN_ERROR";

	[JsonPropertyName("message")]
	public string Message { get; set; } = "An unexpected error occurred.";

	[JsonPropertyName("details")]
	public string? Details { get; set; }

	[JsonPropertyName("timestamp")]
	public DateTime Timestamp { get; set; } = DateTime.UtcNow;

	[JsonPropertyName("isRecoverable")]
	public bool IsRecoverable { get; set; } = true;

	[JsonPropertyName("traceId")]
	public string? TraceId { get; set; }

	public static ApiErrorResponse FromException(Exception exception, string? traceId = null)
	{
		return new ApiErrorResponse
		{
			ErrorCode = GetErrorCode(exception),
			Message = exception.Message,
			Details = GetDetails(exception),
			IsRecoverable = GetIsRecoverable(exception),
			TraceId = traceId
		};
	}

	private static string GetErrorCode(Exception exception)
	{
		return exception switch
		{
			Exceptions.VibeSwarmException vex => vex.ErrorCode,
			UnauthorizedAccessException => "UNAUTHORIZED",
			ArgumentException => "INVALID_ARGUMENT",
			InvalidOperationException => "INVALID_OPERATION",
			TimeoutException => "TIMEOUT",
			OperationCanceledException => "CANCELLED",
			System.Text.Json.JsonException => "JSON_PARSE_ERROR",
			System.Net.Http.HttpRequestException => "HTTP_ERROR",
			_ => "UNKNOWN_ERROR"
		};
	}

	private static string? GetDetails(Exception exception)
	{
		return exception switch
		{
			Exceptions.GitException gitEx => $"Working Directory: {gitEx.WorkingDirectory}, Command: {gitEx.GitCommand}",
			Exceptions.CliAgentException cliEx => $"Provider: {cliEx.ProviderName}, Command: {cliEx.Command}, Exit Code: {cliEx.ExitCode}",
			Exceptions.FileSystemException fsEx => $"Path: {fsEx.Path}, Operation: {fsEx.Operation}",
			Exceptions.EntityNotFoundException entEx => $"Entity Type: {entEx.EntityType}, ID: {entEx.EntityId}",
			Exceptions.ApiException apiEx => $"Status Code: {apiEx.StatusCode}",
			Exceptions.JsonParseException jsonEx => $"Target Type: {jsonEx.TargetType?.Name}",
			_ => null
		};
	}

	private static bool GetIsRecoverable(Exception exception)
	{
		return exception switch
		{
			Exceptions.VibeSwarmException vex => vex.IsRecoverable,
			UnauthorizedAccessException => true,
			ArgumentException => true,
			InvalidOperationException => true,
			TimeoutException => true,
			OperationCanceledException => true,
			_ => true
		};
	}
}
