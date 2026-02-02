using System.Net;
using System.Text.Json;
using VibeSwarm.Shared.Exceptions;
using VibeSwarm.Shared.Models;

namespace VibeSwarm.Web.Middleware;

/// <summary>
/// Global exception handling middleware that catches all unhandled exceptions
/// and returns standardized API error responses
/// </summary>
public class ExceptionHandlingMiddleware
{
	private readonly RequestDelegate _next;
	private readonly ILogger<ExceptionHandlingMiddleware> _logger;
	private readonly IWebHostEnvironment _environment;

	public ExceptionHandlingMiddleware(
		RequestDelegate next,
		ILogger<ExceptionHandlingMiddleware> logger,
		IWebHostEnvironment environment)
	{
		_next = next;
		_logger = logger;
		_environment = environment;
	}

	public async Task InvokeAsync(HttpContext context)
	{
		try
		{
			await _next(context);
		}
		catch (Exception ex)
		{
			await HandleExceptionAsync(context, ex);
		}
	}

	private async Task HandleExceptionAsync(HttpContext context, Exception exception)
	{
		var traceId = context.TraceIdentifier;

		// Log the exception with appropriate level
		LogException(exception, traceId);

		// Don't modify the response if it has already started
		if (context.Response.HasStarted)
		{
			_logger.LogWarning("Response has already started, unable to modify response for exception: {Message}", exception.Message);
			return;
		}

		// Determine status code and create response
		var statusCode = GetStatusCode(exception);
		var response = CreateErrorResponse(exception, traceId);

		// Write the response
		context.Response.StatusCode = statusCode;
		context.Response.ContentType = "application/json";

		var options = new JsonSerializerOptions
		{
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase
		};

		await context.Response.WriteAsync(JsonSerializer.Serialize(response, options));
	}

	private void LogException(Exception exception, string traceId)
	{
		var logLevel = exception switch
		{
			EntityNotFoundException => LogLevel.Warning,
			PathNotFoundException => LogLevel.Warning,
			OperationCanceledException => LogLevel.Information,
			VibeSwarmException vex when vex.IsRecoverable => LogLevel.Warning,
			_ => LogLevel.Error
		};

		_logger.Log(logLevel, exception, "[TraceId: {TraceId}] {ExceptionType}: {Message}",
			traceId,
			exception.GetType().Name,
			exception.Message);
	}

	private static int GetStatusCode(Exception exception)
	{
		return exception switch
		{
			EntityNotFoundException => (int)HttpStatusCode.NotFound,
			PathNotFoundException => (int)HttpStatusCode.NotFound,
			PathAccessDeniedException => (int)HttpStatusCode.Forbidden,
			UnauthorizedAccessException => (int)HttpStatusCode.Unauthorized,
			ArgumentNullException => (int)HttpStatusCode.BadRequest,
			ArgumentException => (int)HttpStatusCode.BadRequest,
			InvalidOperationException => (int)HttpStatusCode.BadRequest,
			NotAGitRepositoryException => (int)HttpStatusCode.BadRequest,
			GitNotAvailableException => (int)HttpStatusCode.ServiceUnavailable,
			CliAgentNotAvailableException => (int)HttpStatusCode.ServiceUnavailable,
			CliAgentAuthenticationException => (int)HttpStatusCode.Unauthorized,
			CliAgentUsageLimitException => (int)HttpStatusCode.TooManyRequests,
			TimeoutException => (int)HttpStatusCode.GatewayTimeout,
			OperationCanceledException => 499, // Client Closed Request
			JsonParseException => (int)HttpStatusCode.UnprocessableEntity,
			System.Text.Json.JsonException => (int)HttpStatusCode.UnprocessableEntity,
			_ => (int)HttpStatusCode.InternalServerError
		};
	}

	private ApiErrorResponse CreateErrorResponse(Exception exception, string traceId)
	{
		var response = ApiErrorResponse.FromException(exception, traceId);

		// In development, include additional details
		if (_environment.IsDevelopment())
		{
			response.Details = $"{response.Details}\n\nStack Trace:\n{exception.StackTrace}";
		}

		return response;
	}
}

/// <summary>
/// Extension methods for adding exception handling middleware
/// </summary>
public static class ExceptionHandlingMiddlewareExtensions
{
	public static IApplicationBuilder UseVibeSwarmExceptionHandling(this IApplicationBuilder builder)
	{
		return builder.UseMiddleware<ExceptionHandlingMiddleware>();
	}
}
