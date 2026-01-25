using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using VibeSwarm.Shared.Data;

namespace VibeSwarm.Web.Pages.Account;

public class LoginModel : PageModel
{
	private readonly SignInManager<ApplicationUser> _signInManager;
	private readonly UserManager<ApplicationUser> _userManager;
	private readonly ILogger<LoginModel> _logger;

	public LoginModel(
		SignInManager<ApplicationUser> signInManager,
		UserManager<ApplicationUser> userManager,
		ILogger<LoginModel> logger)
	{
		_signInManager = signInManager;
		_userManager = userManager;
		_logger = logger;
	}

	[BindProperty]
	public InputModel Input { get; set; } = new();

	public string? ReturnUrl { get; set; }

	public class InputModel
	{
		[Required(ErrorMessage = "Username is required")]
		public string Username { get; set; } = "";

		[Required(ErrorMessage = "Password is required")]
		public string Password { get; set; } = "";

		public bool RememberMe { get; set; }
	}

	public void OnGet(string? returnUrl = null)
	{
		ReturnUrl = returnUrl ?? "/";
	}

	public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
	{
		returnUrl ??= "/";

		// Prevent redirect loops
		if (returnUrl.Contains("/login", StringComparison.OrdinalIgnoreCase) ||
			returnUrl.Contains("/logout", StringComparison.OrdinalIgnoreCase) ||
			returnUrl.Contains("/account/login", StringComparison.OrdinalIgnoreCase) ||
			returnUrl.Contains("/account/logout", StringComparison.OrdinalIgnoreCase))
		{
			returnUrl = "/";
		}

		if (!ModelState.IsValid)
		{
			return RedirectToPage("/login", new { error = "Invalid input", returnUrl });
		}

		try
		{
			var result = await _signInManager.PasswordSignInAsync(
				Input.Username,
				Input.Password,
				Input.RememberMe,
				lockoutOnFailure: true);

			if (result.Succeeded)
			{
				_logger.LogInformation("User {Username} logged in successfully", Input.Username);

				// Update last login timestamp
				var user = await _userManager.FindByNameAsync(Input.Username);
				if (user != null)
				{
					user.LastLoginAt = DateTime.UtcNow;
					await _userManager.UpdateAsync(user);
				}

				return LocalRedirect(returnUrl);
			}

			if (result.IsLockedOut)
			{
				_logger.LogWarning("User {Username} account locked out", Input.Username);
				return Redirect($"/login?error=locked&returnUrl={Uri.EscapeDataString(returnUrl)}");
			}

			if (result.IsNotAllowed)
			{
				_logger.LogWarning("User {Username} is not allowed to sign in", Input.Username);
				return Redirect($"/login?error=notallowed&returnUrl={Uri.EscapeDataString(returnUrl)}");
			}

			_logger.LogWarning("Invalid login attempt for user {Username}", Input.Username);
			return Redirect($"/login?error=invalid&returnUrl={Uri.EscapeDataString(returnUrl)}");
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Error during login for user {Username}", Input.Username);
			return Redirect($"/login?error=exception&returnUrl={Uri.EscapeDataString(returnUrl)}");
		}
	}
}
