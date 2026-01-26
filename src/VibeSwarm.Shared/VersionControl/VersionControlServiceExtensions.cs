using Microsoft.Extensions.DependencyInjection;

namespace VibeSwarm.Shared.VersionControl;

/// <summary>
/// Extension methods for registering version control services.
/// </summary>
public static class VersionControlServiceExtensions
{
	/// <summary>
	/// Adds version control services to the service collection.
	/// </summary>
	/// <param name="services">The service collection.</param>
	/// <returns>The service collection for chaining.</returns>
	public static IServiceCollection AddVersionControlServices(this IServiceCollection services)
	{
		services.AddSingleton<IGitCommandExecutor, GitCommandExecutor>();
		services.AddSingleton<IVersionControlService, VersionControlService>();
		return services;
	}
}
