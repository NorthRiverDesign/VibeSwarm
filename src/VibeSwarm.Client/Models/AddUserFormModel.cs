using System.ComponentModel.DataAnnotations;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Client.Models;

/// <summary>
/// Form model for adding a new user
/// </summary>
public class AddUserFormModel
{
	[Required(ErrorMessage = "Username is required.")]
	[StringLength(50, MinimumLength = 3, ErrorMessage = "Username must be between 3 and 50 characters.")]
	public string UserName { get; set; } = string.Empty;

	[EmailAddress(ErrorMessage = "Invalid email address.")]
	public string? Email { get; set; }

	[Required(ErrorMessage = "Password is required.")]
	[MinLength(8, ErrorMessage = "Password must be at least 8 characters.")]
	public string Password { get; set; } = string.Empty;

	public string Role { get; set; } = UserRoles.User;
}
