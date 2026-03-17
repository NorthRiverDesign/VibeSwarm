using Microsoft.AspNetCore.Identity;
using VibeSwarm.Shared.Data;
using VibeSwarm.Web.Services;

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
        HttpContext httpContext,
        ILogger<Program> logger)
    {
        await signInManager.SignOutAsync();
        ThemePreferenceCookieHelper.Delete(httpContext.Response, httpContext.Request);
        logger.LogInformation("User logged out");
        return Results.Redirect("/login");
    }
}
