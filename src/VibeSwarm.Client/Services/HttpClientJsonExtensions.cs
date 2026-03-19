using System.Net;
using System.Net.Http.Json;

namespace VibeSwarm.Client.Services;

/// <summary>
/// Safe JSON deserialization extensions for HttpClient.
/// These methods handle empty responses (204 No Content) and null JSON bodies
/// that would otherwise cause <see cref="System.Text.Json.JsonException"/> in
/// <see cref="HttpClientJsonExtensions.GetFromJsonAsync{T}"/>.
///
/// Use these instead of the built-in GetFromJsonAsync/ReadFromJsonAsync when the
/// server endpoint can return null, 204, or an empty body.
/// </summary>
internal static class SafeHttpClientJsonExtensions
{
	/// <summary>
	/// GET request that safely deserializes JSON. Returns <paramref name="fallback"/>
	/// when the response is 204 No Content or has an empty body.
	/// </summary>
	public static async Task<T> GetJsonAsync<T>(
		this HttpClient http,
		string requestUri,
		T fallback,
		CancellationToken ct = default)
	{
		var response = await http.GetAsync(requestUri, ct);

		if (response.StatusCode == HttpStatusCode.NoContent)
			return fallback;

		response.EnsureSuccessStatusCode();

		if (response.Content.Headers.ContentLength == 0)
			return fallback;

		var result = await response.Content.ReadFromJsonAsync<T>(ct);
		return result ?? fallback;
	}

	/// <summary>
	/// GET request that safely deserializes JSON for nullable types.
	/// Returns null when the response is 204 No Content or has an empty body.
	/// </summary>
	public static async Task<T?> GetJsonOrNullAsync<T>(
		this HttpClient http,
		string requestUri,
		CancellationToken ct = default) where T : class
	{
		var response = await http.GetAsync(requestUri, ct);

		if (response.StatusCode == HttpStatusCode.NoContent)
			return null;

		response.EnsureSuccessStatusCode();

		if (response.Content.Headers.ContentLength == 0)
			return null;

		return await response.Content.ReadFromJsonAsync<T>(ct);
	}

	/// <summary>
	/// GET request that safely deserializes a value type from JSON.
	/// Returns <paramref name="fallback"/> when the response is 204 or empty.
	/// </summary>
	public static async Task<T> GetJsonValueAsync<T>(
		this HttpClient http,
		string requestUri,
		T fallback,
		CancellationToken ct = default) where T : struct
	{
		var response = await http.GetAsync(requestUri, ct);

		if (response.StatusCode == HttpStatusCode.NoContent)
			return fallback;

		response.EnsureSuccessStatusCode();

		if (response.Content.Headers.ContentLength == 0)
			return fallback;

		return await response.Content.ReadFromJsonAsync<T>(ct);
	}

	/// <summary>
	/// Safely reads JSON from a response body. Returns <paramref name="fallback"/>
	/// when the body is empty or the status is 204.
	/// </summary>
	public static async Task<T> ReadJsonAsync<T>(
		this HttpResponseMessage response,
		T fallback,
		CancellationToken ct = default)
	{
		if (response.StatusCode == HttpStatusCode.NoContent)
			return fallback;

		if (response.Content.Headers.ContentLength == 0)
			return fallback;

		var result = await response.Content.ReadFromJsonAsync<T>(ct);
		return result ?? fallback;
	}

	/// <summary>
	/// Safely reads JSON from a response body for nullable types.
	/// Returns null when the body is empty or the status is 204.
	/// </summary>
	public static async Task<T?> ReadJsonOrNullAsync<T>(
		this HttpResponseMessage response,
		CancellationToken ct = default) where T : class
	{
		if (response.StatusCode == HttpStatusCode.NoContent)
			return null;

		if (response.Content.Headers.ContentLength == 0)
			return null;

		return await response.Content.ReadFromJsonAsync<T>(ct);
	}
}
