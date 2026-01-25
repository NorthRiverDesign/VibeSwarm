using Microsoft.AspNetCore.Components;

namespace VibeSwarm.Web.Pages;

public partial class Login
{
    [Inject]
    private NavigationManager NavigationManager { get; set; } = default!;

    [SupplyParameterFromQuery]
    public string? ReturnUrl { get; set; }

    [SupplyParameterFromQuery(Name = "error")]
    public string? Error { get; set; }

    private string? ErrorMessage { get; set; }
    private bool NoUsersExist { get; set; }

    protected override void OnInitialized()
    {
        // Check if the no-users warning flag is set
        NoUsersExist = Environment.GetEnvironmentVariable("VIBESWARM_NO_USERS") == "true";

        // Map error codes to user-friendly messages
        ErrorMessage = Error switch
        {
            "invalid" => "Invalid username or password.",
            "locked" => "Account locked due to multiple failed login attempts. Please try again in 15 minutes.",
            "notallowed" => "Account is not allowed to sign in.",
            "exception" => "An error occurred during login. Please try again.",
            _ => null
        };
    }
}
