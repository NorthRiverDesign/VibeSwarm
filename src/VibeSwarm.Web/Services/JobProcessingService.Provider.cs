using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Services;

public partial class JobProcessingService
{
    /// <summary>
    /// Records usage from an execution result and checks for exhaustion warnings.
    /// </summary>
    private async Task RecordUsageAndCheckExhaustionAsync(
        Guid providerId,
        string providerName,
        Guid jobId,
        ExecutionResult result,
        IProvider provider,
        CancellationToken cancellationToken)
    {
        try
        {
            using var usageScope = _scopeFactory.CreateScope();
            var providerUsageService = usageScope.ServiceProvider.GetService<IProviderUsageService>();
            if (providerUsageService == null)
                return;

            var usageResult = await RefreshProviderUsageAsync(provider, result, cancellationToken);

            // Record the usage
            await providerUsageService.RecordUsageAsync(providerId, jobId, usageResult, cancellationToken);

            // Check for exhaustion warning and broadcast via SignalR
            var warning = await providerUsageService.CheckExhaustionAsync(providerId, cancellationToken: cancellationToken);
            if (warning != null && _jobUpdateService != null)
            {
                await _jobUpdateService.NotifyProviderUsageWarning(
                    providerId,
                    providerName,
                    warning.PercentUsed,
                    warning.Message,
                    warning.IsExhausted,
                    warning.ResetTime);

                if (warning.IsExhausted)
                {
                    _logger.LogWarning("Provider {ProviderName} has reached usage limit after job {JobId}", providerName, jobId);
                }
                else
                {
                    _logger.LogInformation("Provider {ProviderName} is at {PercentUsed}% usage after job {JobId}",
                        providerName, warning.PercentUsed, jobId);
                }
            }
        }
        catch (Exception ex)
        {
            // Don't fail job processing due to usage tracking errors
            _logger.LogWarning(ex, "Failed to record usage for job {JobId}", jobId);
        }
    }

    private async Task<ExecutionResult> RefreshProviderUsageAsync(
        IProvider provider,
        ExecutionResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            var latestLimits = await provider.GetUsageLimitsAsync(cancellationToken);
            if (ShouldApplyProviderUsage(latestLimits))
            {
                result.DetectedUsageLimits = MergeUsageLimits(result.DetectedUsageLimits, latestLimits);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to refresh provider usage snapshot for provider {ProviderId}", provider.Id);
        }

        return result;
    }

    private async Task<string?> ValidateProviderAvailabilityAsync(
        Guid jobId,
        Provider providerConfig,
        IProvider provider,
        CancellationToken cancellationToken)
    {
        if (!providerConfig.IsEnabled)
        {
            return "Provider is disabled";
        }

        _logger.LogInformation("Running preflight health checks for job {JobId} against provider {ProviderName}",
            jobId, providerConfig.Name);

        var isConnected = await provider.TestConnectionAsync(cancellationToken);
        if (!isConnected)
        {
            var errorMessage = "Preflight check failed: Could not connect to provider. ";
            if (!string.IsNullOrEmpty(provider.LastConnectionError))
            {
                errorMessage += provider.LastConnectionError;
                _logger.LogWarning("Provider connection test failed for job {JobId}: {Error}", jobId, provider.LastConnectionError);
            }
            else
            {
                errorMessage += "Ensure the CLI is installed and accessible from the host system.";
                _logger.LogWarning("Provider connection test failed for job {JobId} with no error details", jobId);
            }

            return errorMessage;
        }

        var providerInfo = await provider.GetProviderInfoAsync(cancellationToken);
        if (providerInfo.AdditionalInfo.TryGetValue("isAvailable", out var isAvailableObj) &&
            isAvailableObj is bool isAvailable &&
            !isAvailable)
        {
            return providerInfo.AdditionalInfo.TryGetValue("unavailableReason", out var reasonObj)
                ? reasonObj?.ToString() ?? "Provider not available"
                : "Provider not available";
        }

        using var usageScope = _scopeFactory.CreateScope();
        var providerUsageService = usageScope.ServiceProvider.GetService<IProviderUsageService>();
        if (providerUsageService != null)
        {
            var exhaustionWarning = await providerUsageService.CheckExhaustionAsync(providerConfig.Id, cancellationToken: cancellationToken);
            if (exhaustionWarning?.IsExhausted == true)
            {
                var reason = $"Provider usage limit exhausted: {exhaustionWarning.Message}";
                _logger.LogWarning("Job {JobId} blocked due to provider usage exhaustion: {Message}", jobId, exhaustionWarning.Message);

                if (_jobUpdateService != null)
                {
                    await _jobUpdateService.NotifyProviderUsageWarning(
                        providerConfig.Id,
                        providerConfig.Name,
                        exhaustionWarning.PercentUsed,
                        exhaustionWarning.Message,
                        exhaustionWarning.IsExhausted,
                        exhaustionWarning.ResetTime);
                }

                return reason;
            }
        }

        return null;
    }

    private static bool ShouldApplyProviderUsage(UsageLimits? limits)
    {
        return limits != null && (
            limits.IsLimitReached ||
            limits.CurrentUsage.HasValue ||
            limits.MaxUsage.HasValue ||
            limits.ResetTime.HasValue ||
            limits.Windows.Count > 0);
    }

    private static UsageLimits MergeUsageLimits(UsageLimits? existing, UsageLimits latest)
    {
        return UsageLimitWindowHelper.Merge(existing, latest);
    }

    private static bool ShouldUsePlanningStage(Project? project)
    {
        return project?.PlanningEnabled == true && project.PlanningProviderId.HasValue;
    }

    /// <summary>
    /// Bare mode requires a direct API key (ANTHROPIC_API_KEY). When the provider has no API key
    /// configured, Claude CLI uses OAuth session auth which --bare disables.
    /// </summary>
    private static bool ShouldUseClaudeBareMode(Provider config)
    {
        return !string.IsNullOrWhiteSpace(config.ApiKey)
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));
    }

    private static IProvider CreateProviderInstance(Provider config)
    {
        return (config.Type, config.ConnectionMode) switch
        {
            (ProviderType.Claude, ProviderConnectionMode.SDK) => new ClaudeSdkProvider(config),
            (ProviderType.Copilot, ProviderConnectionMode.SDK) => new CopilotSdkProvider(config),
            (ProviderType.OpenCode, _) => new OpenCodeProvider(config),
            (ProviderType.Claude, _) => new ClaudeProvider(config),
            (ProviderType.Copilot, _) => new CopilotProvider(config),
            _ => throw new NotSupportedException($"Provider type {config.Type} with mode {config.ConnectionMode} is not supported.")
        };
    }

    private static async Task RecordProviderAttemptAsync(
        Guid jobId,
        Guid providerId,
        string providerName,
        string? modelId,
        int attemptOrder,
        string reason,
        VibeSwarmDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var attempt = await dbContext.JobProviderAttempts
            .FirstOrDefaultAsync(a => a.JobId == jobId && a.AttemptOrder == attemptOrder, cancellationToken);

        if (attempt != null)
        {
            attempt.ProviderId = providerId;
            attempt.ProviderName = providerName;
            attempt.ModelId = modelId;
            attempt.Reason = reason;
            attempt.AttemptedAt = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        dbContext.JobProviderAttempts.Add(new JobProviderAttempt
        {
            JobId = jobId,
            ProviderId = providerId,
            ProviderName = providerName,
            ModelId = modelId,
            AttemptOrder = attemptOrder,
            Reason = reason,
            WasSuccessful = false,
            AttemptedAt = DateTime.UtcNow
        });

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static async Task UpdateProviderAttemptOutcomeAsync(
        Guid jobId,
        int attemptOrder,
        bool wasSuccessful,
        string? modelId,
        VibeSwarmDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var attempt = await dbContext.JobProviderAttempts
            .FirstOrDefaultAsync(a => a.JobId == jobId && a.AttemptOrder == attemptOrder, cancellationToken);

        if (attempt == null)
        {
            return;
        }

        attempt.WasSuccessful = wasSuccessful;
        attempt.ModelId = modelId ?? attempt.ModelId;
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
