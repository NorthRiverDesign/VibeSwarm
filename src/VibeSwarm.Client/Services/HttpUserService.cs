using System.Net.Http.Json;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Client.Services;

public class HttpUserService : IUserService
{
    private readonly HttpClient _http;
    public HttpUserService(HttpClient http) => _http = http;

    public async Task<IEnumerable<UserDto>> GetAllUsersAsync()
        => await _http.GetFromJsonAsync<List<UserDto>>("/api/users") ?? [];

    public async Task<UserDto?> GetUserByIdAsync(Guid id)
        => await _http.GetFromJsonAsync<UserDto>($"/api/users/{id}");

    public async Task<(bool Success, string? Error, UserDto? User)> CreateUserAsync(CreateUserModel model)
    {
        var response = await _http.PostAsJsonAsync("/api/users", model);
        if (response.IsSuccessStatusCode)
        {
            var user = await response.Content.ReadFromJsonAsync<UserDto>();
            return (true, null, user);
        }
        var error = await response.Content.ReadAsStringAsync();
        return (false, error, null);
    }

    public async Task<(bool Success, string? Error)> UpdateUserAsync(Guid id, UpdateUserModel model)
    {
        var response = await _http.PutAsJsonAsync($"/api/users/{id}", model);
        if (response.IsSuccessStatusCode) return (true, null);
        var error = await response.Content.ReadAsStringAsync();
        return (false, error);
    }

    public async Task<(bool Success, string? Error)> ResetPasswordAsync(Guid id, string newPassword)
    {
        var response = await _http.PostAsJsonAsync($"/api/users/{id}/reset-password", new { NewPassword = newPassword });
        if (response.IsSuccessStatusCode) return (true, null);
        var error = await response.Content.ReadAsStringAsync();
        return (false, error);
    }

    public async Task<(bool Success, string? Error)> DeleteUserAsync(Guid id)
    {
        var response = await _http.DeleteAsync($"/api/users/{id}");
        if (response.IsSuccessStatusCode) return (true, null);
        var error = await response.Content.ReadAsStringAsync();
        return (false, error);
    }

    public async Task<(bool Success, string? Error)> ToggleUserActiveAsync(Guid id, Guid currentUserId)
    {
        var response = await _http.PostAsJsonAsync($"/api/users/{id}/toggle-active", new { CurrentUserId = currentUserId });
        if (response.IsSuccessStatusCode) return (true, null);
        var error = await response.Content.ReadAsStringAsync();
        return (false, error);
    }

    public async Task<IEnumerable<string>> GetUserRolesAsync(Guid id)
        => await _http.GetFromJsonAsync<List<string>>($"/api/users/{id}/roles") ?? [];
}
