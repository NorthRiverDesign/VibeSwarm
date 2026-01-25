using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;
using VibeSwarm.Web.Hubs;
using VibeSwarm.Web.Middleware;
using VibeSwarm.Web.Services;
using VibeSwarm.Worker;

// Load environment variables from .env file
DotNetEnv.Env.Load();

var builder = WebApplication.CreateBuilder(args);

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
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always; // HTTPS only
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    options.LoginPath = "/login";
    options.LogoutPath = "/logout";
    options.AccessDeniedPath = "/access-denied";
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

app.UseSecurityHeaders();
app.UseStaticFiles();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapBlazorHub();
app.MapHub<JobHub>("/jobhub");
app.MapFallbackToPage("/_Host");

app.Run();
