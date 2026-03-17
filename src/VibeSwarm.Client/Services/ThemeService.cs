using System.Net;
using System.Net.Http.Json;
using Microsoft.JSInterop;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;

namespace VibeSwarm.Client.Services;

public sealed class ThemeService
{
	private readonly HttpClient _httpClient;
	private readonly IJSRuntime _jsRuntime;
	private readonly ILogger<ThemeService> _logger;

	public ThemeService(HttpClient httpClient, IJSRuntime jsRuntime, ILogger<ThemeService> logger)
	{
		_httpClient = httpClient;
		_jsRuntime = jsRuntime;
		_logger = logger;
	}

	public async Task<ThemePreference> GetThemePreferenceAsync(CancellationToken cancellationToken = default)
	{
		try
		{
			var response = await _httpClient.GetAsync("/api/auth/theme-preference", cancellationToken);
			if (response.IsSuccessStatusCode)
			{
				var themePreference = await response.Content.ReadFromJsonAsync<ThemePreferenceDto>(cancellationToken);
				if (themePreference is not null)
				{
					return ThemePreferenceExtensions.ParseOrDefault(themePreference.Theme);
				}
			}
			else if (response.StatusCode != HttpStatusCode.Unauthorized && response.StatusCode != HttpStatusCode.Forbidden)
			{
				_logger.LogWarning("Unexpected response while loading theme preference: {StatusCode}", response.StatusCode);
			}
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to load persisted theme preference");
		}

		return await GetBrowserThemePreferenceAsync();
	}

	public async Task UpdateThemePreferenceAsync(ThemePreference preference, CancellationToken cancellationToken = default)
	{
		var previousPreference = await GetBrowserThemePreferenceAsync();
		await _jsRuntime.InvokeVoidAsync("vibeSwarmTheme.setPreference", cancellationToken, preference.ToValue(), true);

		try
		{
			var response = await _httpClient.PutAsJsonAsync(
				"/api/auth/theme-preference",
				new UpdateThemePreferenceRequest { Theme = preference.ToValue() },
				cancellationToken);

			await HttpResponseErrorHelper.EnsureSuccessAsync(response, cancellationToken);
		}
		catch
		{
			await _jsRuntime.InvokeVoidAsync("vibeSwarmTheme.setPreference", cancellationToken, previousPreference.ToValue(), true);
			throw;
		}
	}

	private async Task<ThemePreference> GetBrowserThemePreferenceAsync()
	{
		try
		{
			var preference = await _jsRuntime.InvokeAsync<string>("vibeSwarmTheme.getPreference");
			return ThemePreferenceExtensions.ParseOrDefault(preference);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to read browser theme preference");
			return ThemePreference.System;
		}
	}
}
