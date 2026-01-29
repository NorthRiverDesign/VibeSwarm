using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Services;

public class UserService : IUserService
{
	private readonly UserManager<ApplicationUser> _userManager;
	private readonly RoleManager<IdentityRole<Guid>> _roleManager;
	private readonly ILogger<UserService> _logger;

	public UserService(
		UserManager<ApplicationUser> userManager,
		RoleManager<IdentityRole<Guid>> roleManager,
		ILogger<UserService> logger)
	{
		_userManager = userManager;
		_roleManager = roleManager;
		_logger = logger;
	}

	public async Task<IEnumerable<UserDto>> GetAllUsersAsync()
	{
		var users = await _userManager.Users.OrderBy(u => u.UserName).ToListAsync();
		var result = new List<UserDto>();

		foreach (var user in users)
		{
			var roles = await _userManager.GetRolesAsync(user);
			result.Add(new UserDto
			{
				Id = user.Id,
				UserName = user.UserName ?? string.Empty,
				Email = user.Email,
				IsActive = user.IsActive,
				CreatedAt = user.CreatedAt,
				LastLoginAt = user.LastLoginAt,
				Roles = roles
			});
		}

		return result;
	}

	public async Task<UserDto?> GetUserByIdAsync(Guid id)
	{
		var user = await _userManager.FindByIdAsync(id.ToString());
		if (user == null) return null;

		var roles = await _userManager.GetRolesAsync(user);
		return new UserDto
		{
			Id = user.Id,
			UserName = user.UserName ?? string.Empty,
			Email = user.Email,
			IsActive = user.IsActive,
			CreatedAt = user.CreatedAt,
			LastLoginAt = user.LastLoginAt,
			Roles = roles
		};
	}

	public async Task<(bool Success, string? Error, UserDto? User)> CreateUserAsync(CreateUserModel model)
	{
		try
		{
			// Check if username already exists
			var existingUser = await _userManager.FindByNameAsync(model.UserName);
			if (existingUser != null)
			{
				return (false, "A user with this username already exists.", null);
			}

			var user = new ApplicationUser
			{
				UserName = model.UserName,
				Email = string.IsNullOrWhiteSpace(model.Email) ? $"{model.UserName}@vibeswarm.local" : model.Email,
				EmailConfirmed = true,
				IsActive = true,
				CreatedAt = DateTime.UtcNow
			};

			var result = await _userManager.CreateAsync(user, model.Password);

			if (!result.Succeeded)
			{
				var errors = string.Join(" ", result.Errors.Select(e => e.Description));
				_logger.LogWarning("Failed to create user '{UserName}': {Errors}", model.UserName, errors);
				return (false, errors, null);
			}

			// Assign role
			var role = model.Role == DatabaseSeeder.AdminRole ? DatabaseSeeder.AdminRole : DatabaseSeeder.UserRole;
			await _userManager.AddToRoleAsync(user, role);

			_logger.LogInformation("Created user '{UserName}' with role '{Role}'", model.UserName, role);

			var roles = await _userManager.GetRolesAsync(user);
			var userDto = new UserDto
			{
				Id = user.Id,
				UserName = user.UserName ?? string.Empty,
				Email = user.Email,
				IsActive = user.IsActive,
				CreatedAt = user.CreatedAt,
				LastLoginAt = user.LastLoginAt,
				Roles = roles
			};

			return (true, null, userDto);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Error creating user '{UserName}'", model.UserName);
			return (false, "An unexpected error occurred.", null);
		}
	}

	public async Task<(bool Success, string? Error)> UpdateUserAsync(Guid id, UpdateUserModel model)
	{
		try
		{
			var user = await _userManager.FindByIdAsync(id.ToString());
			if (user == null)
			{
				return (false, "User not found.");
			}

			if (!string.IsNullOrWhiteSpace(model.Email))
			{
				user.Email = model.Email;
				var result = await _userManager.UpdateAsync(user);
				if (!result.Succeeded)
				{
					var errors = string.Join(" ", result.Errors.Select(e => e.Description));
					return (false, errors);
				}
			}

			if (!string.IsNullOrWhiteSpace(model.Role))
			{
				// Remove all existing roles
				var currentRoles = await _userManager.GetRolesAsync(user);
				await _userManager.RemoveFromRolesAsync(user, currentRoles);

				// Add new role
				var newRole = model.Role == DatabaseSeeder.AdminRole ? DatabaseSeeder.AdminRole : DatabaseSeeder.UserRole;
				await _userManager.AddToRoleAsync(user, newRole);

				_logger.LogInformation("Updated role for user '{UserName}' to '{Role}'", user.UserName, newRole);
			}

			return (true, null);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Error updating user {UserId}", id);
			return (false, "An unexpected error occurred.");
		}
	}

	public async Task<(bool Success, string? Error)> ResetPasswordAsync(Guid id, string newPassword)
	{
		try
		{
			var user = await _userManager.FindByIdAsync(id.ToString());
			if (user == null)
			{
				return (false, "User not found.");
			}

			// Remove existing password and set new one
			var token = await _userManager.GeneratePasswordResetTokenAsync(user);
			var result = await _userManager.ResetPasswordAsync(user, token, newPassword);

			if (!result.Succeeded)
			{
				var errors = string.Join(" ", result.Errors.Select(e => e.Description));
				_logger.LogWarning("Failed to reset password for user '{UserName}': {Errors}", user.UserName, errors);
				return (false, errors);
			}

			_logger.LogInformation("Password reset for user '{UserName}'", user.UserName);
			return (true, null);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Error resetting password for user {UserId}", id);
			return (false, "An unexpected error occurred.");
		}
	}

	public async Task<(bool Success, string? Error)> DeleteUserAsync(Guid id)
	{
		try
		{
			var user = await _userManager.FindByIdAsync(id.ToString());
			if (user == null)
			{
				return (false, "User not found.");
			}

			// Prevent deleting the last admin
			var roles = await _userManager.GetRolesAsync(user);
			if (roles.Contains(DatabaseSeeder.AdminRole))
			{
				var adminUsers = await _userManager.GetUsersInRoleAsync(DatabaseSeeder.AdminRole);
				if (adminUsers.Count <= 1)
				{
					return (false, "Cannot delete the last administrator account.");
				}
			}

			var result = await _userManager.DeleteAsync(user);

			if (!result.Succeeded)
			{
				var errors = string.Join(" ", result.Errors.Select(e => e.Description));
				return (false, errors);
			}

			_logger.LogInformation("Deleted user '{UserName}'", user.UserName);
			return (true, null);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Error deleting user {UserId}", id);
			return (false, "An unexpected error occurred.");
		}
	}

	public async Task<(bool Success, string? Error)> ToggleUserActiveAsync(Guid id)
	{
		try
		{
			var user = await _userManager.FindByIdAsync(id.ToString());
			if (user == null)
			{
				return (false, "User not found.");
			}

			// Prevent deactivating the last active admin
			if (user.IsActive)
			{
				var roles = await _userManager.GetRolesAsync(user);
				if (roles.Contains(DatabaseSeeder.AdminRole))
				{
					var adminUsers = await _userManager.GetUsersInRoleAsync(DatabaseSeeder.AdminRole);
					var activeAdmins = adminUsers.Count(u => u.IsActive);
					if (activeAdmins <= 1)
					{
						return (false, "Cannot deactivate the last active administrator account.");
					}
				}
			}

			user.IsActive = !user.IsActive;
			var result = await _userManager.UpdateAsync(user);

			if (!result.Succeeded)
			{
				var errors = string.Join(" ", result.Errors.Select(e => e.Description));
				return (false, errors);
			}

			_logger.LogInformation("Toggled active status for user '{UserName}' to {IsActive}", user.UserName, user.IsActive);
			return (true, null);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Error toggling active status for user {UserId}", id);
			return (false, "An unexpected error occurred.");
		}
	}

	public async Task<IEnumerable<string>> GetUserRolesAsync(Guid id)
	{
		var user = await _userManager.FindByIdAsync(id.ToString());
		if (user == null) return [];

		return await _userManager.GetRolesAsync(user);
	}
}
