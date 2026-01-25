using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace VibeSwarm.Shared.Data;

public static class DatabaseSeeder
{
    public static async Task InitializeAdminUserAsync(
        UserManager<ApplicationUser> userManager,
        IConfiguration configuration,
        ILogger logger)
    {
        // Validate configuration and warn about missing credentials
        ValidateConfiguration(configuration, logger);

        // Read admin credentials from environment variables or configuration
        var adminUsername = Environment.GetEnvironmentVariable("DEFAULT_ADMIN_USER")
            ?? configuration["DEFAULT_ADMIN_USER"]
            ?? "admin";

        var adminPassword = Environment.GetEnvironmentVariable("DEFAULT_ADMIN_PASS")
            ?? configuration["DEFAULT_ADMIN_PASS"];

        // Check if admin user already exists
        var existingAdmin = await userManager.FindByNameAsync(adminUsername);
        if (existingAdmin != null)
        {
            logger.LogInformation("Admin user '{Username}' already exists", adminUsername);
            return;
        }

        // Generate a secure random password if not provided
        bool passwordWasGenerated = false;
        if (string.IsNullOrEmpty(adminPassword))
        {
            logger.LogWarning(
                "====================================================\n" +
                "SECURITY WARNING: Application is MISCONFIGURED!\n" +
                "DEFAULT_ADMIN_PASS environment variable is NOT SET.\n" +
                "This is a security risk for production deployments.\n" +
                "====================================================");

            adminPassword = GenerateSecurePassword();
            passwordWasGenerated = true;
        }

        // Create the admin user
        var adminUser = new ApplicationUser
        {
            UserName = adminUsername,
            Email = $"{adminUsername}@vibeswarm.local",
            EmailConfirmed = true,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        var result = await userManager.CreateAsync(adminUser, adminPassword);

        if (result.Succeeded)
        {
            logger.LogInformation("Admin user '{Username}' created successfully", adminUsername);

            if (passwordWasGenerated)
            {
                logger.LogWarning(
                    "====================================================\n" +
                    "IMPORTANT: No DEFAULT_ADMIN_PASS was set!\n" +
                    "A temporary password has been generated.\n" +
                    "Username: {Username}\n" +
                    "Password: {Password}\n" +
                    "Please save this password - it will not be shown again!\n" +
                    "Set DEFAULT_ADMIN_PASS in .env file for production.\n" +
                    "====================================================",
                    adminUsername,
                    adminPassword);
            }
            else
            {
                logger.LogInformation("Admin user created with password from environment/configuration");
            }
        }
        else
        {
            var errors = string.Join(", ", result.Errors.Select(e => e.Description));
            logger.LogError("Failed to create admin user: {Errors}", errors);
        }
    }

    private static string GenerateSecurePassword()
    {
        // Generate a secure random password that meets the default Identity requirements
        // Requirements: 8+ chars, uppercase, lowercase, digit
        const string uppercase = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lowercase = "abcdefghijkmnopqrstuvwxyz";
        const string digits = "23456789";
        const string all = uppercase + lowercase + digits;

        var random = new Random();
        var password = new char[16];

        // Ensure at least one of each required character type
        password[0] = uppercase[random.Next(uppercase.Length)];
        password[1] = lowercase[random.Next(lowercase.Length)];
        password[2] = digits[random.Next(digits.Length)];

        // Fill the rest randomly
        for (int i = 3; i < password.Length; i++)
        {
            password[i] = all[random.Next(all.Length)];
        }

        // Shuffle the password
        return new string(password.OrderBy(x => random.Next()).ToArray());
    }

    /// <summary>
    /// Validates that authentication configuration is properly set up
    /// </summary>
    private static void ValidateConfiguration(IConfiguration configuration, ILogger logger)
    {
        var adminUserEnv = Environment.GetEnvironmentVariable("DEFAULT_ADMIN_USER");
        var adminPassEnv = Environment.GetEnvironmentVariable("DEFAULT_ADMIN_PASS");
        var adminUserConfig = configuration["DEFAULT_ADMIN_USER"];
        var adminPassConfig = configuration["DEFAULT_ADMIN_PASS"];

        var hasUserConfigured = !string.IsNullOrWhiteSpace(adminUserEnv) || !string.IsNullOrWhiteSpace(adminUserConfig);
        var hasPasswordConfigured = !string.IsNullOrWhiteSpace(adminPassEnv) || !string.IsNullOrWhiteSpace(adminPassConfig);

        if (!hasUserConfigured && !hasPasswordConfigured)
        {
            logger.LogWarning(
                "====================================================\n" +
                "CONFIGURATION WARNING:\n" +
                "Neither DEFAULT_ADMIN_USER nor DEFAULT_ADMIN_PASS are configured.\n" +
                "Using default username 'admin' and auto-generated password.\n" +
                "\n" +
                "For production deployments, you MUST configure these:\n" +
                "1. Create a .env file in the application root\n" +
                "2. Set DEFAULT_ADMIN_USER=your-username\n" +
                "3. Set DEFAULT_ADMIN_PASS=your-secure-password\n" +
                "\n" +
                "OR set them as environment variables in your deployment.\n" +
                "====================================================");
        }
        else if (!hasPasswordConfigured)
        {
            logger.LogWarning(
                "====================================================\n" +
                "SECURITY WARNING:\n" +
                "DEFAULT_ADMIN_PASS is NOT configured!\n" +
                "A random password will be generated and shown ONCE in logs.\n" +
                "\n" +
                "For production: Set DEFAULT_ADMIN_PASS in .env file or environment.\n" +
                "====================================================");
        }
        else if (!hasUserConfigured)
        {
            logger.LogInformation(
                "Using default admin username 'admin'. " +
                "To customize, set DEFAULT_ADMIN_USER in .env or environment.");
        }
        else
        {
            logger.LogInformation("Admin credentials configured from environment/configuration");
        }
    }
}
