using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using VibeSwarm.Shared.Data;

namespace VibeSwarm.Web.Pages;

/// <summary>
/// Setup page for creating the initial administrator account.
/// This page is only accessible when no users exist in the database
/// and no DEFAULT_ADMIN credentials are configured via environment variables.
/// </summary>
[AllowAnonymous]
[IgnoreAntiforgeryToken]
public class SetupModel : PageModel
{
	private readonly UserManager<ApplicationUser> _userManager;
	private readonly RoleManager<IdentityRole<Guid>> _roleManager;
	private readonly SignInManager<ApplicationUser> _signInManager;
	private readonly ILogger<SetupModel> _logger;

	public SetupModel(
		UserManager<ApplicationUser> userManager,
		RoleManager<IdentityRole<Guid>> roleManager,
		SignInManager<ApplicationUser> signInManager,
		ILogger<SetupModel> logger)
	{
		_userManager = userManager;
		_roleManager = roleManager;
		_signInManager = signInManager;
		_logger = logger;
	}

	[BindProperty]
	[Required(ErrorMessage = "Username is required")]
	[StringLength(50, MinimumLength = 3, ErrorMessage = "Username must be between 3 and 50 characters")]
	[RegularExpression(@"^[a-zA-Z][a-zA-Z0-9._-]*$", ErrorMessage = "Username must start with a letter and contain only letters, numbers, dots, underscores, or hyphens")]
	public string Username { get; set; } = "";

	[BindProperty]
	[EmailAddress(ErrorMessage = "Invalid email address")]
	public string? Email { get; set; }

	[BindProperty]
	[Required(ErrorMessage = "Password is required")]
	[StringLength(100, MinimumLength = 8, ErrorMessage = "Password must be at least 8 characters")]
	public string Password { get; set; } = "";

	[BindProperty]
	[Required(ErrorMessage = "Please confirm your password")]
	[Compare("Password", ErrorMessage = "Passwords do not match")]
	public string ConfirmPassword { get; set; } = "";

	public string? ErrorMessage { get; set; }

	public List<string>? ValidationErrors { get; set; }

	public IActionResult OnGet()
	{
		// Check if setup is still needed
		if (!IsSetupRequired())
		{
			_logger.LogInformation("Setup page accessed but setup is not required, redirecting to login");
			return Redirect("/login");
		}

		return Page();
	}

	public async Task<IActionResult> OnPostAsync()
	{
		// Double-check that setup is still required (prevent race conditions)
		if (!IsSetupRequired())
		{
			_logger.LogWarning("Setup form submitted but setup is no longer required");
			return Redirect("/login");
		}

		ValidationErrors = new List<string>();

		// Validate model state
		if (!ModelState.IsValid)
		{
			foreach (var modelState in ModelState.Values)
			{
				foreach (var error in modelState.Errors)
				{
					ValidationErrors.Add(error.ErrorMessage);
				}
			}
			return Page();
		}

		// Validate passwords match
		if (Password != ConfirmPassword)
		{
			ValidationErrors.Add("Passwords do not match");
			return Page();
		}

		try
		{
			// Ensure roles exist
			await DatabaseSeeder.InitializeRolesAsync(_roleManager, _logger);

			// Create the admin user
			var adminUser = new ApplicationUser
			{
				UserName = Username,
				Email = string.IsNullOrWhiteSpace(Email) ? $"{Username}@vibeswarm.local" : Email,
				EmailConfirmed = true,
				IsActive = true,
				CreatedAt = DateTime.UtcNow
			};

			var result = await _userManager.CreateAsync(adminUser, Password);

			if (result.Succeeded)
			{
				// Assign Admin role to the first user
				await _userManager.AddToRoleAsync(adminUser, DatabaseSeeder.AdminRole);

				_logger.LogInformation(
					"Initial admin user '{Username}' created successfully via setup page",
					Username);

				// Update the environment flag
				Environment.SetEnvironmentVariable("VIBESWARM_NO_USERS", "false");
				Environment.SetEnvironmentVariable("VIBESWARM_SETUP_REQUIRED", "false");

				// Sign in the user automatically
				await _signInManager.SignInAsync(adminUser, isPersistent: false);

				_logger.LogInformation("Admin user '{Username}' signed in after setup", Username);

				return Redirect("/");
			}
			else
			{
				// Identity validation failed - extract meaningful error messages
				foreach (var error in result.Errors)
				{
					ValidationErrors.Add(error.Description);
					_logger.LogWarning("User creation error: {ErrorCode} - {Description}",
						error.Code, error.Description);
				}
				return Page();
			}
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Error creating admin user during setup");
			ErrorMessage = "An unexpected error occurred while creating the admin account. Please try again.";
			return Page();
		}
	}

	/// <summary>
	/// Checks if setup is required (no users exist and no env credentials configured).
	/// </summary>
	private bool IsSetupRequired()
	{
		// Check if any users exist
		var hasUsers = _userManager.Users.Any();
		if (hasUsers)
		{
			return false;
		}

		// Check if env vars are configured (setup not required if they are)
		var hasEnvUser = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DEFAULT_ADMIN_USER"));
		var hasEnvPass = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DEFAULT_ADMIN_PASS"));

		// Setup is required only if no users exist AND no credentials are configured
		// Note: If credentials ARE configured, the seeder will handle user creation
		return !hasEnvUser || !hasEnvPass;
	}
}
