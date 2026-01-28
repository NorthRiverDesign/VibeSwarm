using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace VibeSwarm.Shared.Data;

public static class DatabaseSeeder
{
    /// <summary>
    /// Initializes the admin user if credentials are provided via environment variables.
    /// If no credentials are configured, the setup page will be used instead.
    /// </summary>
    public static async Task InitializeAdminUserAsync(
        UserManager<ApplicationUser> userManager,
        IConfiguration configuration,
        ILogger logger)
    {
        // Check if any users already exist
        if (userManager.Users.Any())
        {
            logger.LogInformation("Users already exist in the database, skipping admin initialization");
            return;
        }

        // Check if credentials are configured
        var (hasCredentials, adminUsername, adminPassword) = GetConfiguredCredentials(configuration);

        if (!hasCredentials)
        {
            // No credentials configured - defer to setup page
            logger.LogInformation(
                "====================================================\n" +
                "SETUP REQUIRED:\n" +
                "No admin credentials configured (DEFAULT_ADMIN_USER/DEFAULT_ADMIN_PASS).\n" +
                "The application will redirect to the setup page for initial configuration.\n" +
                "====================================================");

            // Set environment flag to indicate setup is required
            Environment.SetEnvironmentVariable("VIBESWARM_SETUP_REQUIRED", "true");
            return;
        }

        // Credentials are configured - create the admin user
        logger.LogInformation("Admin credentials found, creating admin user '{Username}'", adminUsername);

        var adminUser = new ApplicationUser
        {
            UserName = adminUsername,
            Email = $"{adminUsername}@vibeswarm.local",
            EmailConfirmed = true,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        var result = await userManager.CreateAsync(adminUser, adminPassword!);

        if (result.Succeeded)
        {
            logger.LogInformation("Admin user '{Username}' created successfully from environment configuration", adminUsername);
            Environment.SetEnvironmentVariable("VIBESWARM_SETUP_REQUIRED", "false");
        }
        else
        {
            var errors = string.Join(", ", result.Errors.Select(e => e.Description));
            logger.LogError(
                "====================================================\n" +
                "ADMIN USER CREATION FAILED!\n" +
                "Errors: {Errors}\n" +
                "Please check your DEFAULT_ADMIN_PASS meets password requirements:\n" +
                "- Minimum 8 characters\n" +
                "- At least one uppercase letter\n" +
                "- At least one lowercase letter\n" +
                "- At least one digit\n" +
                "====================================================",
                errors);

            // Set flag to indicate setup is needed due to invalid credentials
            Environment.SetEnvironmentVariable("VIBESWARM_SETUP_REQUIRED", "true");
        }
    }

    /// <summary>
    /// Gets the configured admin credentials from environment variables or configuration.
    /// Returns a tuple indicating if valid credentials are configured, and the username/password if so.
    /// </summary>
    private static (bool HasCredentials, string Username, string? Password) GetConfiguredCredentials(IConfiguration configuration)
    {
        var adminUserEnv = Environment.GetEnvironmentVariable("DEFAULT_ADMIN_USER");
        var adminPassEnv = Environment.GetEnvironmentVariable("DEFAULT_ADMIN_PASS");
        var adminUserConfig = configuration["DEFAULT_ADMIN_USER"];
        var adminPassConfig = configuration["DEFAULT_ADMIN_PASS"];

        var adminUsername = adminUserEnv ?? adminUserConfig;
        var adminPassword = adminPassEnv ?? adminPassConfig;

        // Both username and password must be configured for automatic user creation
        var hasCredentials = !string.IsNullOrWhiteSpace(adminUsername) && !string.IsNullOrWhiteSpace(adminPassword);

        // Default username to "admin" if only password is provided
        if (string.IsNullOrWhiteSpace(adminUsername) && !string.IsNullOrWhiteSpace(adminPassword))
        {
            adminUsername = "admin";
            hasCredentials = true;
        }

        return (hasCredentials, adminUsername ?? "admin", adminPassword);
    }

    /// <summary>
    /// Checks if setup is required (no users exist and no env credentials configured).
    /// This can be called from middleware to determine if redirect to setup is needed.
    /// </summary>
    public static bool IsSetupRequired(UserManager<ApplicationUser> userManager, IConfiguration configuration)
    {
        // If users exist, setup is not required
        if (userManager.Users.Any())
        {
            return false;
        }

        // Check if credentials are configured
        var (hasCredentials, _, _) = GetConfiguredCredentials(configuration);

        // Setup is required if no users exist and no credentials are configured
        return !hasCredentials;
    }
}
