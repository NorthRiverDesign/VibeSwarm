using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Identity;
using VibeSwarm.Shared.Data;

namespace VibeSwarm.Web.Pages;

public partial class Login
{
    [Inject]
    private SignInManager<ApplicationUser> SignInManager { get; set; } = default!;

    [Inject]
    private UserManager<ApplicationUser> UserManager { get; set; } = default!;

    [Inject]
    private NavigationManager NavigationManager { get; set; } = default!;

    [SupplyParameterFromQuery]
    public string? ReturnUrl { get; set; }

    private LoginInputModel LoginModel { get; set; } = new();
    private string? ErrorMessage { get; set; }
    private bool IsLoading { get; set; }
    private bool NoUsersExist { get; set; }

    protected override void OnInitialized()
    {
        // Check if the no-users warning flag is set
        NoUsersExist = Environment.GetEnvironmentVariable("VIBESWARM_NO_USERS") == "true";
    }

    private async Task HandleLogin()
    {
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var result = await SignInManager.PasswordSignInAsync(
                LoginModel.Username,
                LoginModel.Password,
                LoginModel.RememberMe,
                lockoutOnFailure: true);

            if (result.Succeeded)
            {
                // Update last login timestamp
                var user = await UserManager.FindByNameAsync(LoginModel.Username);
                if (user != null)
                {
                    user.LastLoginAt = DateTime.UtcNow;
                    await UserManager.UpdateAsync(user);
                }

                // Redirect to return URL or home page
                // Prevent redirect loops by not redirecting back to login pages
                var redirectUrl = "/";
                if (!string.IsNullOrEmpty(ReturnUrl) &&
                    !ReturnUrl.Contains("/login", StringComparison.OrdinalIgnoreCase) &&
                    !ReturnUrl.Contains("/logout", StringComparison.OrdinalIgnoreCase))
                {
                    redirectUrl = ReturnUrl;
                }

                NavigationManager.NavigateTo(redirectUrl, forceLoad: true);
            }
            else if (result.IsLockedOut)
            {
                ErrorMessage = "Account locked due to multiple failed login attempts. Please try again in 15 minutes.";
            }
            else if (result.IsNotAllowed)
            {
                ErrorMessage = "Account is not allowed to sign in.";
            }
            else
            {
                ErrorMessage = "Invalid username or password.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"An error occurred during login: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private class LoginInputModel
    {
        [Required(ErrorMessage = "Username is required")]
        public string Username { get; set; } = "";

        [Required(ErrorMessage = "Password is required")]
        public string Password { get; set; } = "";

        public bool RememberMe { get; set; }
    }
}
