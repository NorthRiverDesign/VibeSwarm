using VibeSwarm.Shared.Services;

namespace VibeSwarm.Shared.Providers;

/// <summary>
/// Base class for SDK-based providers that use official NuGet SDKs
/// instead of spawning CLI processes. Implements IAsyncDisposable
/// for proper SDK client lifecycle management.
/// </summary>
public abstract class SdkProviderBase : ProviderBase, IAsyncDisposable
{
	protected readonly string? ApiKey;
	protected readonly string? ApiEndpoint;
	protected readonly string? ExecutablePath;
	protected readonly string? WorkingDirectory;

	protected SdkProviderBase(Provider config)
		: base(config.Id, config.Name, ProviderConnectionMode.SDK)
	{
		ApiKey = config.ApiKey;
		ApiEndpoint = config.ApiEndpoint;
		ExecutablePath = config.ExecutablePath;
		WorkingDirectory = config.WorkingDirectory;
	}

	/// <summary>
	/// SDK providers do not support CLI updates.
	/// </summary>
	public override Task<CliUpdateResult> UpdateCliAsync(CancellationToken cancellationToken = default)
	{
		return Task.FromResult(CliUpdateResult.Fail("CLI updates are not applicable for SDK mode. Update the NuGet package instead."));
	}

	/// <summary>
	/// Dispose SDK clients and resources.
	/// </summary>
	public abstract ValueTask DisposeAsync();
}
