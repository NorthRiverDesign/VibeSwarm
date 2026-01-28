using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using VibeSwarm.Shared.Data;

namespace VibeSwarm.Web.Pages;

[AllowAnonymous]
[IgnoreAntiforgeryToken]
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
    [Required]
    public string Username { get; set; } = "";

    [BindProperty]
    [Required]
    public string Password { get; set; } = "";

    [BindProperty]
    public bool RememberMe { get; set; }

    [FromQuery]
    public string? ReturnUrl { get; set; }

    [FromQuery(Name = "error")]
    public string? Error { get; set; }

    public string? ErrorMessage { get; set; }

    public bool NoUsersExist { get; set; }

    public IActionResult OnGet()
    {
        // Check if setup is required and redirect
        var setupRequired = Environment.GetEnvironmentVariable("VIBESWARM_SETUP_REQUIRED") == "true";
        if (setupRequired)
        {
            return Redirect("/setup");
        }

        NoUsersExist = Environment.GetEnvironmentVariable("VIBESWARM_NO_USERS") == "true";

        ErrorMessage = Error switch
        {
            "invalid" => "Invalid username or password.",
            "locked" => "Account locked due to multiple failed login attempts. Please try again in 15 minutes.",
            "notallowed" => "Account is not allowed to sign in.",
            "exception" => "An error occurred during login. Please try again.",
            _ => null
        };

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var returnUrl = ReturnUrl ?? "/";

        // Prevent redirect loops
        if (returnUrl.Contains("/login", StringComparison.OrdinalIgnoreCase) ||
            returnUrl.Contains("/logout", StringComparison.OrdinalIgnoreCase) ||
            returnUrl.Contains("/account/", StringComparison.OrdinalIgnoreCase))
        {
            returnUrl = "/";
        }

        if (!ModelState.IsValid)
        {
            return Redirect($"/login?error=invalid&returnUrl={Uri.EscapeDataString(returnUrl)}");
        }

        try
        {
            var result = await _signInManager.PasswordSignInAsync(
                Username, Password, RememberMe, lockoutOnFailure: true);

            if (result.Succeeded)
            {
                _logger.LogInformation("User {Username} logged in successfully", Username);

                var user = await _userManager.FindByNameAsync(Username);
                if (user is not null)
                {
                    user.LastLoginAt = DateTime.UtcNow;
                    await _userManager.UpdateAsync(user);
                }

                return LocalRedirect(returnUrl);
            }

            if (result.IsLockedOut)
            {
                _logger.LogWarning("User {Username} account locked out", Username);
                return Redirect($"/login?error=locked&returnUrl={Uri.EscapeDataString(returnUrl)}");
            }

            if (result.IsNotAllowed)
            {
                _logger.LogWarning("User {Username} is not allowed to sign in", Username);
                return Redirect($"/login?error=notallowed&returnUrl={Uri.EscapeDataString(returnUrl)}");
            }

            _logger.LogWarning("Invalid login attempt for user {Username}", Username);
            return Redirect($"/login?error=invalid&returnUrl={Uri.EscapeDataString(returnUrl)}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during login for user {Username}", Username);
            return Redirect($"/login?error=exception&returnUrl={Uri.EscapeDataString(returnUrl)}");
        }
    }
}
