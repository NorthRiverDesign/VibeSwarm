using System.Net.Http.Json;
using System.Text.Json;
using VibeSwarm.Shared.Models;

namespace VibeSwarm.Client.Services;

/// <summary>
/// Extension methods for HttpClient that provide safer JSON operations with error handling
/// </summary>
public static class HttpClientExtensions
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = true
	};

	/// <summary>
	/// Safely gets JSON data from the specified URI with error handling
	/// </summary>
	/// <typeparam name="T">The type to deserialize to</typeparam>
	/// <param name="client">The HTTP client</param>
	/// <param name="requestUri">The URI to request</param>
	/// <param name="defaultValue">Default value to return on failure</param>
	/// <param name="ct">Cancellation token</param>
	/// <returns>The deserialized object or default value on failure</returns>
	public static async Task<T?> SafeGetFromJsonAsync<T>(
		this HttpClient client,
		string requestUri,
		T? defaultValue = default,
		CancellationToken ct = default)
	{
		try
		{
			var response = await client.GetAsync(requestUri, ct);

			if (!response.IsSuccessStatusCode)
			{
				await LogApiError(response, requestUri);
				return defaultValue;
			}

			var content = await response.Content.ReadAsStringAsync(ct);

			if (string.IsNullOrWhiteSpace(content))
			{
				return defaultValue;
			}

			// Try to detect if the response is an error object
			if (IsErrorResponse(content))
			{
				var errorResponse = JsonSerializer.Deserialize<ApiErrorResponse>(content, JsonOptions);
				Console.WriteLine($"[API Error] {requestUri}: {errorResponse?.Message} (Code: {errorResponse?.ErrorCode})");
				return defaultValue;
			}

			return JsonSerializer.Deserialize<T>(content, JsonOptions);
		}
		catch (JsonException ex)
		{
			Console.WriteLine($"[JSON Parse Error] {requestUri}: {ex.Message}");
			return defaultValue;
		}
		catch (HttpRequestException ex)
		{
			Console.WriteLine($"[HTTP Error] {requestUri}: {ex.Message}");
			return defaultValue;
		}
		catch (TaskCanceledException)
		{
			// Request was cancelled, don't log as error
			return defaultValue;
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Unexpected Error] {requestUri}: {ex.Message}");
			return defaultValue;
		}
	}

	/// <summary>
	/// Safely gets JSON data from the specified URI, returning a result wrapper
	/// </summary>
	/// <typeparam name="T">The type to deserialize to</typeparam>
	/// <param name="client">The HTTP client</param>
	/// <param name="requestUri">The URI to request</param>
	/// <param name="ct">Cancellation token</param>
	/// <returns>A result wrapper containing either the data or error information</returns>
	public static async Task<HttpResult<T>> SafeGetWithResultAsync<T>(
		this HttpClient client,
		string requestUri,
		CancellationToken ct = default)
	{
		try
		{
			var response = await client.GetAsync(requestUri, ct);
			var content = await response.Content.ReadAsStringAsync(ct);

			if (!response.IsSuccessStatusCode)
			{
				var errorResponse = TryParseErrorResponse(content);
				return HttpResult<T>.Failure(
					errorResponse?.Message ?? $"Request failed with status {response.StatusCode}",
					errorResponse?.ErrorCode ?? "HTTP_ERROR",
					(int)response.StatusCode
				);
			}

			if (string.IsNullOrWhiteSpace(content))
			{
				return HttpResult<T>.Success(default!);
			}

			// Check if response is an error
			if (IsErrorResponse(content))
			{
				var errorResponse = JsonSerializer.Deserialize<ApiErrorResponse>(content, JsonOptions);
				return HttpResult<T>.Failure(
					errorResponse?.Message ?? "Unknown API error",
					errorResponse?.ErrorCode ?? "API_ERROR",
					(int)response.StatusCode
				);
			}

			var data = JsonSerializer.Deserialize<T>(content, JsonOptions);
			return HttpResult<T>.Success(data!);
		}
		catch (JsonException ex)
		{
			return HttpResult<T>.Failure($"Failed to parse response: {ex.Message}", "JSON_PARSE_ERROR");
		}
		catch (HttpRequestException ex)
		{
			return HttpResult<T>.Failure($"Network error: {ex.Message}", "NETWORK_ERROR");
		}
		catch (TaskCanceledException)
		{
			return HttpResult<T>.Failure("Request was cancelled", "CANCELLED");
		}
		catch (Exception ex)
		{
			return HttpResult<T>.Failure($"Unexpected error: {ex.Message}", "UNKNOWN_ERROR");
		}
	}

	/// <summary>
	/// Safely posts JSON data and returns the response with error handling
	/// </summary>
	public static async Task<HttpResult<TResponse>> SafePostAsJsonAsync<TRequest, TResponse>(
		this HttpClient client,
		string requestUri,
		TRequest request,
		CancellationToken ct = default)
	{
		try
		{
			var response = await client.PostAsJsonAsync(requestUri, request, ct);
			var content = await response.Content.ReadAsStringAsync(ct);

			if (!response.IsSuccessStatusCode)
			{
				var errorResponse = TryParseErrorResponse(content);
				return HttpResult<TResponse>.Failure(
					errorResponse?.Message ?? $"Request failed with status {response.StatusCode}",
					errorResponse?.ErrorCode ?? "HTTP_ERROR",
					(int)response.StatusCode
				);
			}

			if (string.IsNullOrWhiteSpace(content))
			{
				return HttpResult<TResponse>.Success(default!);
			}

			// Check if response is an error
			if (IsErrorResponse(content))
			{
				var errorResponse = JsonSerializer.Deserialize<ApiErrorResponse>(content, JsonOptions);
				return HttpResult<TResponse>.Failure(
					errorResponse?.Message ?? "Unknown API error",
					errorResponse?.ErrorCode ?? "API_ERROR",
					(int)response.StatusCode
				);
			}

			var data = JsonSerializer.Deserialize<TResponse>(content, JsonOptions);
			return HttpResult<TResponse>.Success(data!);
		}
		catch (JsonException ex)
		{
			return HttpResult<TResponse>.Failure($"Failed to parse response: {ex.Message}", "JSON_PARSE_ERROR");
		}
		catch (HttpRequestException ex)
		{
			return HttpResult<TResponse>.Failure($"Network error: {ex.Message}", "NETWORK_ERROR");
		}
		catch (TaskCanceledException)
		{
			return HttpResult<TResponse>.Failure("Request was cancelled", "CANCELLED");
		}
		catch (Exception ex)
		{
			return HttpResult<TResponse>.Failure($"Unexpected error: {ex.Message}", "UNKNOWN_ERROR");
		}
	}

	private static bool IsErrorResponse(string content)
	{
		// Quick check for common error response patterns
		return content.Contains("\"errorCode\"") && content.Contains("\"success\":false");
	}

	private static ApiErrorResponse? TryParseErrorResponse(string content)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(content))
				return null;

			return JsonSerializer.Deserialize<ApiErrorResponse>(content, JsonOptions);
		}
		catch
		{
			return null;
		}
	}

	private static async Task LogApiError(HttpResponseMessage response, string requestUri)
	{
		var content = await response.Content.ReadAsStringAsync();
		var errorResponse = TryParseErrorResponse(content);

		if (errorResponse != null)
		{
			Console.WriteLine($"[API Error] {requestUri}: {errorResponse.Message} (Code: {errorResponse.ErrorCode}, Status: {response.StatusCode})");
		}
		else
		{
			Console.WriteLine($"[API Error] {requestUri}: Status {response.StatusCode}");
		}
	}
}

/// <summary>
/// Result wrapper for HTTP operations
/// </summary>
/// <typeparam name="T">The type of the data</typeparam>
public class HttpResult<T>
{
	public bool IsSuccess { get; private set; }
	public T? Data { get; private set; }
	public string? ErrorMessage { get; private set; }
	public string? ErrorCode { get; private set; }
	public int? StatusCode { get; private set; }

	public static HttpResult<T> Success(T data) => new()
	{
		IsSuccess = true,
		Data = data
	};

	public static HttpResult<T> Failure(string message, string errorCode, int? statusCode = null) => new()
	{
		IsSuccess = false,
		ErrorMessage = message,
		ErrorCode = errorCode,
		StatusCode = statusCode
	};
}
