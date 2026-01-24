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

    public async Task<Provider?> GetDefaultAsync(CancellationToken cancellationToken = default)
    {
        return await _dbContext.Providers
            .FirstOrDefaultAsync(p => p.IsDefault && p.IsEnabled, cancellationToken);
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
        var result = await TestConnectionWithDetailsAsync(id, cancellationToken);
        return result.IsConnected;
    }

    public async Task<ConnectionTestResult> TestConnectionWithDetailsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var provider = await GetByIdAsync(id, cancellationToken);
        if (provider == null)
        {
            return new ConnectionTestResult
            {
                IsConnected = false,
                ErrorMessage = "Provider not found."
            };
        }

        var instance = CreateProviderInstance(provider);
        var isConnected = await instance.TestConnectionAsync(cancellationToken);

        if (isConnected)
        {
            provider.LastConnectedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return new ConnectionTestResult
        {
            IsConnected = isConnected,
            ErrorMessage = isConnected ? null : instance.LastConnectionError
        };
    }

    public async Task SetEnabledAsync(Guid id, bool isEnabled, CancellationToken cancellationToken = default)
    {
        var provider = await _dbContext.Providers
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        if (provider == null)
        {
            throw new InvalidOperationException($"Provider with ID {id} not found.");
        }

        provider.IsEnabled = isEnabled;

        // If disabling and this was the default provider, clear default status
        if (!isEnabled && provider.IsDefault)
        {
            provider.IsDefault = false;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task SetDefaultAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var provider = await _dbContext.Providers
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        if (provider == null)
        {
            throw new InvalidOperationException($"Provider with ID {id} not found.");
        }

        // Clear all other defaults
        var currentDefaults = await _dbContext.Providers
            .Where(p => p.IsDefault)
            .ToListAsync(cancellationToken);

        foreach (var p in currentDefaults)
        {
            p.IsDefault = false;
        }

        // Set this provider as default and ensure it's enabled
        provider.IsDefault = true;
        provider.IsEnabled = true;

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public IProvider? CreateInstance(Provider config)
    {
        try
        {
            return CreateProviderInstance(config);
        }
        catch
        {
            return null;
        }
    }

    public async Task<SessionSummary> GetSessionSummaryAsync(
        Guid providerId,
        string? sessionId,
        string? workingDirectory = null,
        string? fallbackOutput = null,
        CancellationToken cancellationToken = default)
    {
        var provider = await GetByIdAsync(providerId, cancellationToken);
        if (provider == null)
        {
            return new SessionSummary
            {
                Success = false,
                ErrorMessage = "Provider not found"
            };
        }

        try
        {
            var instance = CreateProviderInstance(provider);
            return await instance.GetSessionSummaryAsync(sessionId, workingDirectory, fallbackOutput, cancellationToken);
        }
        catch (Exception ex)
        {
            return new SessionSummary
            {
                Success = false,
                ErrorMessage = $"Failed to get session summary: {ex.Message}"
            };
        }
    }

    private static IProvider CreateProviderInstance(Provider config)
    {
        return config.Type switch
        {
            ProviderType.OpenCode => new OpenCodeProvider(config),
            ProviderType.Claude => new ClaudeProvider(config),
            ProviderType.Copilot => new CopilotProvider(config),
            _ => throw new NotSupportedException($"Provider type {config.Type} is not supported.")
        };
    }
}
