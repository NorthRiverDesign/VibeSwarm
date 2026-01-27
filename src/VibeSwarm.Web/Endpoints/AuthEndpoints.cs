using Microsoft.AspNetCore.Identity;
using VibeSwarm.Shared.Data;

namespace VibeSwarm.Web.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/account/logout", HandleLogoutAsync);
        return endpoints;
    }

    private static async Task<IResult> HandleLogoutAsync(
        SignInManager<ApplicationUser> signInManager,
        ILogger<Program> logger)
    {
        await signInManager.SignOutAsync();
        logger.LogInformation("User logged out");
        return Results.Redirect("/login");
    }
}
