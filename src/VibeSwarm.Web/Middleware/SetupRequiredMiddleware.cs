namespace VibeSwarm.Web.Middleware;

/// <summary>
/// Middleware that redirects users to the setup page when initial configuration is required.
/// Setup is required when no users exist in the database and no admin credentials
/// are configured via environment variables (DEFAULT_ADMIN_USER/DEFAULT_ADMIN_PASS).
/// </summary>
public class SetupRequiredMiddleware
{
	private readonly RequestDelegate _next;
	private readonly ILogger<SetupRequiredMiddleware> _logger;

	// Paths that should be accessible without completing setup
	private static readonly string[] AllowedPaths = new[]
	{
		"/setup",
		"/lib/",
		"/css/",
		"/js/",
		"/img/",
		"/fonts/",
		"/favicon",
		"/_framework/",
		"/_blazor"
	};

	public SetupRequiredMiddleware(RequestDelegate next, ILogger<SetupRequiredMiddleware> logger)
	{
		_next = next;
		_logger = logger;
	}

	public async Task InvokeAsync(HttpContext context)
	{
		var path = context.Request.Path.Value?.ToLowerInvariant() ?? "/";

		// Allow access to setup page and static resources
		if (IsAllowedPath(path))
		{
			await _next(context);
			return;
		}

		// Check if setup is required (set by the initialization process)
		var setupRequired = Environment.GetEnvironmentVariable("VIBESWARM_SETUP_REQUIRED") == "true";

		if (setupRequired)
		{
			_logger.LogDebug("Setup required, redirecting from {Path} to /setup", path);
			context.Response.Redirect("/setup");
			return;
		}

		await _next(context);
	}

	private static bool IsAllowedPath(string path)
	{
		return AllowedPaths.Any(allowed => path.StartsWith(allowed, StringComparison.OrdinalIgnoreCase));
	}
}

/// <summary>
/// Extension methods for adding the SetupRequiredMiddleware to the application pipeline.
/// </summary>
public static class SetupRequiredMiddlewareExtensions
{
	/// <summary>
	/// Adds the setup required middleware to the application pipeline.
	/// This middleware redirects to /setup when initial configuration is needed.
	/// </summary>
	public static IApplicationBuilder UseSetupRequired(this IApplicationBuilder builder)
	{
		return builder.UseMiddleware<SetupRequiredMiddleware>();
	}
}
