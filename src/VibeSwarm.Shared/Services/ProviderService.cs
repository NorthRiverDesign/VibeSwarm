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

    public async Task<IEnumerable<ProviderModel>> GetModelsAsync(Guid providerId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.ProviderModels
            .Where(m => m.ProviderId == providerId)
            .OrderByDescending(m => m.IsDefault)
            .ThenBy(m => m.DisplayName ?? m.ModelId)
            .ToListAsync(cancellationToken);
    }

    public async Task<IEnumerable<ProviderModel>> RefreshModelsAsync(Guid providerId, CancellationToken cancellationToken = default)
    {
        var provider = await GetByIdAsync(providerId, cancellationToken);
        if (provider == null)
        {
            throw new InvalidOperationException($"Provider with ID {providerId} not found.");
        }

        var instance = CreateProviderInstance(provider);

        // Add a timeout wrapper for the provider info call to prevent indefinite hangs
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        ProviderInfo providerInfo;
        try
        {
            providerInfo = await instance.GetProviderInfoAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out while refreshing models for provider {provider.Name}. The CLI may be unresponsive or require authentication.");
        }

        // Get existing models for this provider
        var existingModels = await _dbContext.ProviderModels
            .Where(m => m.ProviderId == providerId)
            .ToListAsync(cancellationToken);

        // Create a dictionary for quick lookup
        var existingModelDict = existingModels.ToDictionary(m => m.ModelId, m => m);

        // Track which models are still available
        var availableModelIds = new HashSet<string>(providerInfo.AvailableModels);

        // Update existing models and add new ones
        foreach (var modelId in providerInfo.AvailableModels)
        {
            if (existingModelDict.TryGetValue(modelId, out var existingModel))
            {
                // Update existing model
                existingModel.IsAvailable = true;
                existingModel.UpdatedAt = DateTime.UtcNow;

                // Update price multiplier if we have pricing info
                if (providerInfo.Pricing?.ModelMultipliers?.TryGetValue(modelId, out var multiplier) == true)
                {
                    existingModel.PriceMultiplier = multiplier;
                }
            }
            else
            {
                // Add new model
                var newModel = new ProviderModel
                {
                    ProviderId = providerId,
                    ModelId = modelId,
                    DisplayName = FormatModelDisplayName(modelId),
                    IsAvailable = true,
                    IsDefault = providerInfo.AvailableModels.Count == 1 || modelId.Contains("sonnet", StringComparison.OrdinalIgnoreCase),
                    UpdatedAt = DateTime.UtcNow
                };

                if (providerInfo.Pricing?.ModelMultipliers?.TryGetValue(modelId, out var multiplier) == true)
                {
                    newModel.PriceMultiplier = multiplier;
                }

                _dbContext.ProviderModels.Add(newModel);
            }
        }

        // Mark models not in the list as unavailable
        foreach (var existingModel in existingModels)
        {
            if (!availableModelIds.Contains(existingModel.ModelId))
            {
                existingModel.IsAvailable = false;
                existingModel.UpdatedAt = DateTime.UtcNow;
            }
        }

        // Update provider's last refresh timestamp
        provider.LastModelsRefreshAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);

        return await GetModelsAsync(providerId, cancellationToken);
    }

    private static string FormatModelDisplayName(string modelId)
    {
        // Convert model IDs like "claude-sonnet-4-20250514" to "Claude Sonnet 4"
        // or "gpt-4o" to "GPT-4O"
        var name = modelId;

        // Remove date suffixes like -20250514
        var dateMatch = System.Text.RegularExpressions.Regex.Match(name, @"-\d{8}$");
        if (dateMatch.Success)
        {
            name = name[..^dateMatch.Length];
        }

        // Split by common separators and capitalize
        var parts = name.Split(['-', '_', '/'], StringSplitOptions.RemoveEmptyEntries);
        var formattedParts = parts.Select(p =>
        {
            // Handle known abbreviations
            if (p.Equals("gpt", StringComparison.OrdinalIgnoreCase))
                return "GPT";
            if (p.Equals("4o", StringComparison.OrdinalIgnoreCase))
                return "4O";
            if (p.Equals("o1", StringComparison.OrdinalIgnoreCase))
                return "O1";

            // Capitalize first letter
            return char.ToUpper(p[0]) + (p.Length > 1 ? p[1..] : "");
        });

        return string.Join(" ", formattedParts);
    }
}
