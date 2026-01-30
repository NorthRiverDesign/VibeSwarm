using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;
using VibeSwarm.Web;
using VibeSwarm.Web.Endpoints;
using VibeSwarm.Web.Hubs;
using VibeSwarm.Web.Middleware;
using VibeSwarm.Web.Services;
using VibeSwarm.Worker;

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
builder.Services.AddServerSideBlazor();
builder.Services.AddSignalR();
builder.Services.AddWorkerServices();
builder.Services.AddVibeSwarmData(connectionString);

// Add authentication state provider for Blazor Server
builder.Services.AddScoped<AuthenticationStateProvider, RevalidatingIdentityAuthenticationStateProvider<ApplicationUser>>();

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
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    options.LoginPath = "/login";
    options.LogoutPath = "/logout";
    options.AccessDeniedPath = "/access-denied";

    // Important for Blazor Server: Don't redirect on API calls
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

// Register notification service (scoped per circuit for Blazor Server)
builder.Services.AddScoped<NotificationService>();

// Register change password modal service (scoped to coordinate between LoginDisplay and MainLayout)
builder.Services.AddScoped<ChangePasswordModalService>();

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
app.UseSecurityHeaders();
app.UseStaticFiles();
app.UseRouting();

// Add setup required middleware before authentication
// This ensures users are redirected to setup before auth checks
app.UseSetupRequired();

app.UseAuthentication();
app.UseAuthorization();

app.MapAuthEndpoints();
app.MapCertificateEndpoints();
app.MapRazorPages();
app.MapBlazorHub();
app.MapHub<JobHub>("/jobhub");
app.MapFallbackToPage("/_Host");

app.Run();

