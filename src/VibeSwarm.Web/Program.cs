using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;
using VibeSwarm.Web.Endpoints;
using VibeSwarm.Web.Hubs;
using VibeSwarm.Web.Middleware;
using VibeSwarm.Web.Services;

// Load environment variables from .env file (if it exists)
// Walk up from the current directory and base directory to find .env at the repo root
static string? FindEnvFile()
{
    var searchRoots = new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
    foreach (var root in searchRoots)
    {
        var dir = new DirectoryInfo(root);
        while (dir is not null)
        {
            var envFile = Path.Combine(dir.FullName, ".env");
            if (File.Exists(envFile))
                return envFile;
            dir = dir.Parent;
        }
    }
    return null;
}

var envPath = FindEnvFile();
if (envPath is not null)
{
    Console.WriteLine($"Loading environment variables from: {envPath}");
    DotNetEnv.Env.Load(envPath);
}

var builder = WebApplication.CreateBuilder(args);

// Generate self-signed certificate if it doesn't exist
var certPath = Path.Combine(AppContext.BaseDirectory, "vibeswarm.pfx");
var certPassword = "vibeswarm-dev-cert";

if (!File.Exists(certPath))
{
    Console.WriteLine("Generating self-signed certificate for HTTPS...");

    var cert = VibeSwarm.Web.CertificateGenerator.GenerateSelfSignedCertificate("VibeSwarm");
    var certBytes = cert.Export(X509ContentType.Pfx, certPassword);
    File.WriteAllBytes(certPath, certBytes);

    Console.WriteLine($"Self-signed certificate created at: {certPath}");
    Console.WriteLine("Using self-signed certificate. Your browser will show a security warning - this is expected.");
}

// Configure Kestrel HTTPS defaults with the self-signed certificate.
// URL binding is controlled by ASPNETCORE_URLS (set in .env or environment).
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.ConfigureHttpsDefaults(httpsOptions =>
    {
        httpsOptions.ServerCertificate = X509CertificateLoader.LoadPkcs12FromFile(certPath, certPassword);
    });
});

// Fallback URLs if ASPNETCORE_URLS is not set in .env or environment
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    builder.WebHost.UseUrls("http://localhost:5000", "https://localhost:5001");
}

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? "Data Source=vibeswarm.db";

// Add authorization services
builder.Services.AddAuthorization();

builder.Services.AddRazorPages();

// Add controllers for API endpoints with JSON serialization config
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
        options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    });

// Configure SignalR with iOS-optimized timeouts and stateful reconnect support
builder.Services.AddSignalR(options =>
{
    // Server timeout: How long to wait for a message from the client
    // Set higher for iOS which can suspend WebSocket for 10-30 seconds
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);

    // Keep alive interval: How often to ping the client
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);

    // Enable detailed errors in development
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();

    // Allow larger messages for complex state transfers
    options.MaximumReceiveMessageSize = 256 * 1024; // 256KB

    // Stateful reconnect buffer size (for .NET 10 stateful reconnect)
    options.StatefulReconnectBufferSize = 100 * 1024; // 100KB
});

builder.Services.AddWorkerServices();
builder.Services.AddVibeSwarmData(connectionString);

// Add Identity services
builder.Services.AddIdentity<ApplicationUser, IdentityRole<Guid>>(options =>
{
    // Password requirements
    options.Password.RequireDigit = true;
    options.Password.RequireLowercase = true;
    options.Password.RequireUppercase = true;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequiredLength = 8;

    // Lockout settings (brute force protection)
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.AllowedForNewUsers = true;

    // User settings
    options.User.RequireUniqueEmail = false;
    options.SignIn.RequireConfirmedAccount = false;
})
.AddEntityFrameworkStores<VibeSwarmDbContext>()
.AddDefaultTokenProviders();

// Configure authentication cookies
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.Name = "VibeSwarm.Auth";

    // Default expiration for session cookies (when Remember Me is NOT checked)
    // This is the server-side ticket lifetime
    options.ExpireTimeSpan = TimeSpan.FromDays(365); // 1 year for persistent sessions
    options.SlidingExpiration = true; // Refresh the cookie on each request when past halfway

    options.LoginPath = "/login";
    options.LogoutPath = "/logout";
    options.AccessDeniedPath = "/access-denied";

    // Handle persistent cookies for "Remember Me" - this ensures the cookie
    // persists across browser/app restarts, rebuilds, and updates
    options.Events.OnSigningIn = context =>
    {
        if (context.Properties.IsPersistent)
        {
            // When Remember Me is checked, make the cookie last for 1 year
            // The cookie will be refreshed via sliding expiration on each visit
            context.Properties.ExpiresUtc = DateTimeOffset.UtcNow.AddDays(365);
            context.Properties.IsPersistent = true;
        }
        else
        {
            // Session cookie - expires when browser closes
            context.Properties.ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8);
        }
        return Task.CompletedTask;
    };

    // Important: Don't redirect on API calls - return 401 instead
    options.Events.OnRedirectToLogin = context =>
    {
        // If this is an API call or AJAX request, return 401 instead of redirecting
        if (context.Request.Path.StartsWithSegments("/api") ||
            context.Request.Headers["X-Requested-With"] == "XMLHttpRequest")
        {
            context.Response.StatusCode = 401;
            return Task.CompletedTask;
        }

        context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    };
});

// Register SignalR job update service
builder.Services.AddSingleton<IJobUpdateService, SignalRJobUpdateService>();

// Register interaction response service (singleton for cross-service communication)
builder.Services.AddSingleton<IInteractionResponseService, InMemoryInteractionResponseService>();

var app = builder.Build();

// Apply pending migrations on startup and initialize admin user
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
    await dbContext.Database.MigrateAsync();

    // Initialize admin user and roles
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    await DatabaseSeeder.InitializeAdminUserAsync(userManager, roleManager, builder.Configuration, logger);

    // Check if users exist and set appropriate flags
    var userCount = userManager.Users.Count();
    if (userCount == 0)
    {
        // Check if setup is required (no env credentials configured)
        var setupRequired = DatabaseSeeder.IsSetupRequired(userManager, builder.Configuration);

        if (setupRequired)
        {
            logger.LogInformation(
                "====================================================\n" +
                "INITIAL SETUP REQUIRED\n" +
                "Navigate to the application to complete setup.\n" +
                "You will be redirected to create your admin account.\n" +
                "====================================================");
            Environment.SetEnvironmentVariable("VIBESWARM_SETUP_REQUIRED", "true");
        }
        else
        {
            // Credentials were configured but user creation failed
            logger.LogWarning(
                "====================================================\n" +
                "WARNING: No users exist in the database!\n" +
                "Admin user creation may have failed.\n" +
                "Check your DEFAULT_ADMIN_PASS meets password requirements.\n" +
                "====================================================");
        }

        Environment.SetEnvironmentVariable("VIBESWARM_NO_USERS", "true");
    }
    else
    {
        logger.LogInformation("Database initialized with {UserCount} user(s)", userCount);
        Environment.SetEnvironmentVariable("VIBESWARM_NO_USERS", "false");
        Environment.SetEnvironmentVariable("VIBESWARM_SETUP_REQUIRED", "false");
    }
}

// HTTPS redirection disabled - reverse proxy handles TLS termination
// app.UseHttpsRedirection();

// Add global exception handling middleware for API endpoints
app.UseVibeSwarmExceptionHandling();

app.UseSecurityHeaders();

// Serve Blazor WebAssembly static files
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.UseRouting();

// Add setup required middleware before authentication
// This ensures users are redirected to setup before auth checks
app.UseSetupRequired();

app.UseAuthentication();
app.UseAuthorization();

// Map API controllers
app.MapControllers();

// Map auth endpoints and certificate endpoints
app.MapAuthEndpoints();
app.MapCertificateEndpoints();

// Map Razor Pages (Login, Setup)
app.MapRazorPages();

// Map SignalR hub
app.MapHub<JobHub>("/hubs/job");

// Fallback to WASM entry point for client-side routing
app.MapFallbackToFile("index.html");

app.Run();

