using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;

namespace VibeSwarm.Client.Auth;

public class CookieAuthenticationStateProvider : AuthenticationStateProvider
{
	private static readonly AuthenticationState AnonymousAuthenticationState = new(new ClaimsPrincipal(new ClaimsIdentity()));

	private readonly HttpClient _httpClient;
	private readonly ILogger<CookieAuthenticationStateProvider> _logger;
	private readonly SemaphoreSlim _authenticationStateLock = new(1, 1);

	private AuthenticationState _cachedAuthenticationState = AnonymousAuthenticationState;
	private bool _hasCachedAuthenticationState;

	public CookieAuthenticationStateProvider(HttpClient httpClient, ILogger<CookieAuthenticationStateProvider> logger)
	{
		_httpClient = httpClient;
		_logger = logger;
	}

	public override async Task<AuthenticationState> GetAuthenticationStateAsync()
	{
		await _authenticationStateLock.WaitAsync();

		try
		{
			return await GetAuthenticationStateCoreAsync();
		}
		finally
		{
			_authenticationStateLock.Release();
		}
	}

	public async Task<AuthenticationState> RefreshAuthenticationStateAsync()
	{
		var authenticationStateTask = GetAuthenticationStateAsync();
		NotifyAuthenticationStateChanged(authenticationStateTask);
		return await authenticationStateTask;
	}

	public void NotifyAuthenticationStateChanged()
	{
		NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
	}

	private async Task<AuthenticationState> GetAuthenticationStateCoreAsync()
	{
		_logger.LogDebug("Fetching authentication state from /api/auth/user");

		HttpResponseMessage response;

		try
		{
			response = await _httpClient.GetAsync("/api/auth/user");
		}
		catch (HttpRequestException ex)
		{
			return GetFallbackAuthenticationState("Failed to fetch authentication state", ex);
		}
		catch (TaskCanceledException ex)
		{
			return GetFallbackAuthenticationState("Authentication state request timed out", ex);
		}

		_logger.LogDebug("Auth response status: {StatusCode}", response.StatusCode);

		if (response.IsSuccessStatusCode)
		{
			try
			{
				var userInfo = await response.Content.ReadFromJsonAsync<UserInfo>();
				if (userInfo != null)
				{
					var authenticationState = CreateAuthenticatedState(userInfo);
					_cachedAuthenticationState = authenticationState;
					_hasCachedAuthenticationState = true;

					_logger.LogInformation("User authenticated: {UserName}", userInfo.UserName);
					return authenticationState;
				}

				return GetFallbackAuthenticationState("Auth endpoint returned an empty user payload");
			}
			catch (JsonException ex)
			{
				return GetFallbackAuthenticationState("Failed to parse authentication state response", ex);
			}
			catch (NotSupportedException ex)
			{
				return GetFallbackAuthenticationState("Authentication state response content type was not supported", ex);
			}
		}

		if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
		{
			_logger.LogDebug("User is not authenticated ({StatusCode})", response.StatusCode);
			_cachedAuthenticationState = AnonymousAuthenticationState;
			_hasCachedAuthenticationState = true;
			return _cachedAuthenticationState;
		}

		return GetFallbackAuthenticationState($"Unexpected auth response: {response.StatusCode}");
	}

	private AuthenticationState CreateAuthenticatedState(UserInfo userInfo)
	{
		var claims = new List<Claim>
		{
			new(ClaimTypes.Name, userInfo.UserName ?? string.Empty),
			new(ClaimTypes.NameIdentifier, userInfo.UserId ?? string.Empty),
		};

		if (userInfo.Email != null)
		{
			claims.Add(new Claim(ClaimTypes.Email, userInfo.Email));
		}

		foreach (var role in userInfo.Roles ?? [])
		{
			claims.Add(new Claim(ClaimTypes.Role, role));
		}

		var identity = new ClaimsIdentity(claims, "cookie");
		return new AuthenticationState(new ClaimsPrincipal(identity));
	}

	private AuthenticationState GetFallbackAuthenticationState(string reason, Exception? exception = null)
	{
		var cachedUser = _cachedAuthenticationState.User;
		if (_hasCachedAuthenticationState && cachedUser.Identity?.IsAuthenticated == true)
		{
			_logger.LogWarning(exception, "{Reason}. Preserving cached authentication state for {UserName}.", reason, cachedUser.Identity.Name);
			return _cachedAuthenticationState;
		}

		_logger.LogWarning(exception, "{Reason}. Returning anonymous authentication state.", reason);
		_cachedAuthenticationState = AnonymousAuthenticationState;
		_hasCachedAuthenticationState = true;
		return _cachedAuthenticationState;
	}
}

public class UserInfo
{
    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public string? Email { get; set; }
    public List<string> Roles { get; set; } = [];
    public string? ThemePreference { get; set; }
}
