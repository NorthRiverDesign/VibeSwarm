using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;
using VibeSwarm.Web;
using VibeSwarm.Web.Hubs;
using VibeSwarm.Web.Middleware;
using VibeSwarm.Web.Services;
using VibeSwarm.Worker;

// Load environment variables from .env file (if it exists)
if (File.Exists(".env"))
{
    DotNetEnv.Env.Load();
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

// Configure Kestrel to use HTTPS
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.ListenAnyIP(5000); // HTTP
    serverOptions.ListenAnyIP(5001, listenOptions =>
    {
        listenOptions.UseHttps(certPath, certPassword); // HTTPS with self-signed cert
    });
});

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? "Data Source=vibeswarm.db";

// Add global authorization policy - all pages require authentication by default
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build());

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

var app = builder.Build();

// Apply pending migrations on startup and initialize admin user
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
    await dbContext.Database.MigrateAsync();

    // Initialize admin user
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    await DatabaseSeeder.InitializeAdminUserAsync(userManager, builder.Configuration, logger);

    // Verify that at least one user exists
    var userCount = userManager.Users.Count();
    if (userCount == 0)
    {
        logger.LogError(
            "====================================================\n" +
            "CRITICAL ERROR: No users exist in the database!\n" +
            "The application cannot function without at least one user.\n" +
            "Admin user creation may have failed.\n" +
            "Check the logs above for errors.\n" +
            "====================================================");
        throw new InvalidOperationException("No users exist in the database. Cannot start application.");
    }
    else
    {
        logger.LogInformation("Database initialized with {UserCount} user(s)", userCount);
    }
}

app.UseHttpsRedirection();
app.UseSecurityHeaders();
app.UseStaticFiles();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapBlazorHub();
app.MapHub<JobHub>("/jobhub");
app.MapFallbackToPage("/_Host");

app.Run();
