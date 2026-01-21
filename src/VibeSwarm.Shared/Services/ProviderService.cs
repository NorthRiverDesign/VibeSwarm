using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Shared.Services;

public class ProviderService : IProviderService
{
    private readonly VibeSwarmDbContext _dbContext;

    public ProviderService(VibeSwarmDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IEnumerable<Provider>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _dbContext.Providers
            .OrderBy(p => p.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<Provider?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Providers
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
    }

    public async Task<Provider> CreateAsync(Provider provider, CancellationToken cancellationToken = default)
    {
        provider.Id = Guid.NewGuid();
        provider.CreatedAt = DateTime.UtcNow;

        _dbContext.Providers.Add(provider);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return provider;
    }

    public async Task<Provider> UpdateAsync(Provider provider, CancellationToken cancellationToken = default)
    {
        var existing = await _dbContext.Providers
            .FirstOrDefaultAsync(p => p.Id == provider.Id, cancellationToken);

        if (existing == null)
        {
            throw new InvalidOperationException($"Provider with ID {provider.Id} not found.");
        }

        existing.Name = provider.Name;
        existing.Type = provider.Type;
        existing.ConnectionMode = provider.ConnectionMode;
        existing.ExecutablePath = provider.ExecutablePath;
        existing.WorkingDirectory = provider.WorkingDirectory;
        existing.ApiEndpoint = provider.ApiEndpoint;
        existing.ApiKey = provider.ApiKey;
        existing.IsEnabled = provider.IsEnabled;

        await _dbContext.SaveChangesAsync(cancellationToken);

        return existing;
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var provider = await _dbContext.Providers
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        if (provider != null)
        {
            _dbContext.Providers.Remove(provider);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<bool> TestConnectionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var provider = await GetByIdAsync(id, cancellationToken);
        if (provider == null)
        {
            return false;
        }

        var instance = CreateProviderInstance(provider);
        var isConnected = await instance.TestConnectionAsync(cancellationToken);

        if (isConnected)
        {
            provider.LastConnectedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return isConnected;
    }

    private static IProvider CreateProviderInstance(Provider config)
    {
        return config.Type switch
        {
            ProviderType.OpenCode => new OpenCodeProvider(config),
            ProviderType.Claude => new ClaudeProvider(config),
            _ => throw new NotSupportedException($"Provider type {config.Type} is not supported.")
        };
    }
}
