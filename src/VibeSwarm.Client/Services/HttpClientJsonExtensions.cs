using System.Net;
using System.Net.Http.Json;

namespace VibeSwarm.Client.Services;

// Handles 204 No Content and empty bodies that would throw in the built-in ReadFromJsonAsync.
internal static class SafeHttpClientJsonExtensions
{
	public static async Task<T> GetJsonAsync<T>(this HttpClient http, string requestUri, T fallback, CancellationToken ct = default)
	{
		var response = await http.GetAsync(requestUri, ct);
		if (response.StatusCode == HttpStatusCode.NoContent) return fallback;
		response.EnsureSuccessStatusCode();
		if (response.Content.Headers.ContentLength == 0) return fallback;
		return await response.Content.ReadFromJsonAsync<T>(ct) ?? fallback;
	}

	public static async Task<T?> GetJsonOrNullAsync<T>(this HttpClient http, string requestUri, CancellationToken ct = default) where T : class
	{
		var response = await http.GetAsync(requestUri, ct);
		if (response.StatusCode == HttpStatusCode.NoContent) return null;
		response.EnsureSuccessStatusCode();
		if (response.Content.Headers.ContentLength == 0) return null;
		return await response.Content.ReadFromJsonAsync<T>(ct);
	}

	public static async Task<T> GetJsonValueAsync<T>(this HttpClient http, string requestUri, T fallback, CancellationToken ct = default) where T : struct
	{
		var response = await http.GetAsync(requestUri, ct);
		if (response.StatusCode == HttpStatusCode.NoContent) return fallback;
		response.EnsureSuccessStatusCode();
		if (response.Content.Headers.ContentLength == 0) return fallback;
		return await response.Content.ReadFromJsonAsync<T>(ct);
	}

	public static async Task<T> ReadJsonAsync<T>(this HttpResponseMessage response, T fallback, CancellationToken ct = default)
	{
		if (response.StatusCode == HttpStatusCode.NoContent) return fallback;
		if (response.Content.Headers.ContentLength == 0) return fallback;
		return await response.Content.ReadFromJsonAsync<T>(ct) ?? fallback;
	}

	public static async Task<T?> ReadJsonOrNullAsync<T>(this HttpResponseMessage response, CancellationToken ct = default) where T : class
	{
		if (response.StatusCode == HttpStatusCode.NoContent) return null;
		if (response.Content.Headers.ContentLength == 0) return null;
		return await response.Content.ReadFromJsonAsync<T>(ct);
	}
}
