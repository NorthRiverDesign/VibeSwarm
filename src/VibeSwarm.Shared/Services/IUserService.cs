using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Services;

public interface IUserService
{
	Task<IEnumerable<UserDto>> GetAllUsersAsync();
	Task<UserDto?> GetUserByIdAsync(Guid id);
	Task<(bool Success, string? Error, UserDto? User)> CreateUserAsync(CreateUserModel model);
	Task<(bool Success, string? Error)> UpdateUserAsync(Guid id, UpdateUserModel model);
	Task<(bool Success, string? Error)> ResetPasswordAsync(Guid id, string newPassword);
	Task<(bool Success, string? Error)> DeleteUserAsync(Guid id);
	Task<(bool Success, string? Error)> ToggleUserActiveAsync(Guid id, Guid currentUserId);
	Task<IEnumerable<string>> GetUserRolesAsync(Guid id);
}

public class UserDto
{
	public Guid Id { get; set; }
	public string UserName { get; set; } = string.Empty;
	public bool IsActive { get; set; }
	public DateTime CreatedAt { get; set; }
	public DateTime? LastLoginAt { get; set; }
	public IEnumerable<string> Roles { get; set; } = [];
}

public class CreateUserModel
{
	public string UserName { get; set; } = string.Empty;
	public string Password { get; set; } = string.Empty;
	public string Role { get; set; } = UserRoles.User;
}

public class UpdateUserModel
{
	public string? Role { get; set; }
}

/// <summary>
/// User role constants shared across client and server
/// </summary>
public static class UserRoles
{
	public const string Admin = "Admin";
	public const string User = "User";
}
