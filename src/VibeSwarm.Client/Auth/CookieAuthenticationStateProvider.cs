using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace VibeSwarm.Client.Auth;

public class CookieAuthenticationStateProvider : AuthenticationStateProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<CookieAuthenticationStateProvider> _logger;

    public CookieAuthenticationStateProvider(HttpClient httpClient, ILogger<CookieAuthenticationStateProvider> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync("/api/auth/user");

            if (response.IsSuccessStatusCode)
            {
                var userInfo = await response.Content.ReadFromJsonAsync<UserInfo>();
                if (userInfo != null)
                {
                    var claims = new List<Claim>
                    {
                        new(ClaimTypes.Name, userInfo.UserName ?? string.Empty),
                        new(ClaimTypes.NameIdentifier, userInfo.UserId ?? string.Empty),
                    };

                    if (userInfo.Email != null)
                        claims.Add(new Claim(ClaimTypes.Email, userInfo.Email));

                    foreach (var role in userInfo.Roles ?? [])
                    {
                        claims.Add(new Claim(ClaimTypes.Role, role));
                    }

                    var identity = new ClaimsIdentity(claims, "cookie");
                    return new AuthenticationState(new ClaimsPrincipal(identity));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get authentication state");
        }

        return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
    }

    public void NotifyAuthenticationStateChanged()
    {
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }
}

public class UserInfo
{
    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public string? Email { get; set; }
    public List<string> Roles { get; set; } = [];
}
