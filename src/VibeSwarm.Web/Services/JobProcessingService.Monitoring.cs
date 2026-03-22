using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Services;

public partial class JobProcessingService
{
    /// <summary>
    /// Monitors for cancellation requests and sends regular heartbeats.
    /// Does NOT use the passed dbContext - creates its own scopes to avoid disposal issues.
    /// </summary>
    private async Task MonitorCancellationAndHeartbeatAsync(
        Guid jobId,
        JobExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        var heartbeatInterval = TimeSpan.FromSeconds(30); // Reduced frequency to avoid database contention
        var cancellationCheckInterval = TimeSpan.FromSeconds(5); // Check cancellation less frequently
        var lastHeartbeat = DateTime.UtcNow;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Check for cancellation request with a fresh scope
                    using (var checkScope = _scopeFactory.CreateScope())
                    {
                        var checkDbContext = checkScope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
                        var job = await checkDbContext.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);

                        if (job?.CancellationRequested == true)
                        {
                            _logger.LogInformation("Cancellation requested for job {JobId}", jobId);
                            executionContext.CancellationTokenSource?.Cancel();
                            break;
                        }
                    }

                    // Send heartbeat periodically with a fresh scope
                    var now = DateTime.UtcNow;
                    if (now - lastHeartbeat >= heartbeatInterval)
                    {
                        lastHeartbeat = now;
                        using (var heartbeatScope = _scopeFactory.CreateScope())
                        {
                            var heartbeatDbContext = heartbeatScope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
                            var heartbeatJob = await heartbeatDbContext.Jobs.FindAsync(new object[] { jobId }, cancellationToken);
                            if (heartbeatJob != null)
                            {
                                heartbeatJob.LastHeartbeatAt = now;

                                // Persist console output buffer periodically so page refreshes show accumulated output
                                var currentOutput = executionContext.GetConsoleOutput();
                                if (!string.IsNullOrEmpty(currentOutput))
                                {
                                    heartbeatJob.ConsoleOutput = currentOutput;
                                }

                                await heartbeatDbContext.SaveChangesAsync(cancellationToken);
                            }
                        }
                        _logger.LogDebug("Sent heartbeat for job {JobId}", jobId);

                        // Send SignalR heartbeat notification
                        if (_jobUpdateService != null)
                        {
                            try
                            {
                                await _jobUpdateService.NotifyJobHeartbeat(jobId, now);
                            }
                            catch { }
                        }
                    }
                }
                catch (ObjectDisposedException)
                {
                    // Scope was disposed, create a new one on next iteration
                    _logger.LogWarning("DbContext was disposed in heartbeat monitor for job {JobId}, will retry", jobId);
                }

                await Task.Delay(cancellationCheckInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when cancelled
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error in cancellation/heartbeat monitor for job {JobId}", jobId);
        }
    }

    private async Task NotifyStatusChangedAsync(Guid jobId, JobStatus status)
    {
        if (_jobUpdateService != null)
        {
            try
            {
                await _jobUpdateService.NotifyJobStatusChanged(jobId, status.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send status change notification for job {JobId}", jobId);
            }
        }
    }

    private async Task NotifyJobActivityAsync(Guid jobId, string activity, DateTime timestamp)
    {
        if (_jobUpdateService != null)
        {
            try
            {
                await _jobUpdateService.NotifyJobActivity(jobId, activity, timestamp);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send activity notification for job {JobId}", jobId);
            }
        }
    }

    private async Task NotifyJobMessageAddedAsync(Guid jobId)
    {
        if (_jobUpdateService != null)
        {
            try
            {
                await _jobUpdateService.NotifyJobMessageAdded(jobId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send message added notification for job {JobId}", jobId);
            }
        }
    }

    private async Task NotifyJobCompletedAsync(Guid jobId, bool success, string? errorMessage = null)
    {
        if (_jobUpdateService != null)
        {
            try
            {
                await _jobUpdateService.NotifyJobCompleted(jobId, success, errorMessage);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send completion notification for job {JobId}", jobId);
            }
        }

        // Handle idea state when job completes (reset IsProcessing for failed jobs, remove for successful ones)
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var ideaService = scope.ServiceProvider.GetRequiredService<IIdeaService>();
            await ideaService.HandleJobCompletionAsync(jobId, success);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to handle idea completion for job {JobId}", jobId);
        }

        TriggerProcessing();
    }

    private async Task NotifyJobGitDiffUpdatedAsync(Guid jobId, bool hasChanges)
    {
        if (_jobUpdateService != null)
        {
            try
            {
                await _jobUpdateService.NotifyJobGitDiffUpdated(jobId, hasChanges);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send git diff notification for job {JobId}", jobId);
            }
        }
    }

    /// <summary>
    /// Gets temporary execution resources for the given provider, including MCP config and bash env files when needed.
    /// </summary>
	private async Task<(string? McpConfigPath, string? BashEnvPath, List<string>? AdditionalArgs, McpExecutionResources? Resources)> GetMcpExecutionOptionsAsync(
		Guid providerId,
		Project? project,
		string? workingDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var mcpConfigService = scope.ServiceProvider.GetRequiredService<IMcpConfigService>();
            var providerService = scope.ServiceProvider.GetRequiredService<IProviderService>();

            // Get the provider to determine its type
			var provider = await providerService.GetByIdAsync(providerId);
			if (provider == null)
			{
				_logger.LogWarning("Could not find provider {ProviderId} to generate MCP config", providerId);
				return (null, null, null, null);
			}

			var resources = await mcpConfigService.GenerateExecutionResourcesAsync(
				provider.Type,
				project,
				workingDirectory,
				cancellationToken);
			if (!string.IsNullOrEmpty(resources?.ConfigFilePath))
			{
				_logger.LogDebug("Generated MCP config at {ConfigPath} for provider {ProviderId}", resources.ConfigFilePath, providerId);
			}

			return (resources?.ConfigFilePath, resources?.BashEnvFilePath, null, resources);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to generate MCP config for provider {ProviderId}", providerId);
			return (null, null, null, null);
		}
	}

	private void CleanupMcpExecutionResources(McpExecutionResources? resources)
	{
		if (resources == null)
		{
			return;
		}

		try
		{
			using var scope = _scopeFactory.CreateScope();
			var mcpConfigService = scope.ServiceProvider.GetRequiredService<IMcpConfigService>();
			mcpConfigService.CleanupExecutionResources(resources);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to clean up MCP execution resources");
		}
	}

    private async Task<string?> PrepareProjectMemoryFileAsync(Project? project, CancellationToken cancellationToken)
    {
        if (project == null)
        {
            return null;
        }

        using var scope = _scopeFactory.CreateScope();
        var projectMemoryService = scope.ServiceProvider.GetRequiredService<IProjectMemoryService>();
        return await projectMemoryService.PrepareMemoryFileAsync(project, cancellationToken);
    }

    private async Task PersistProjectMemoryAsync(Guid? projectId, string? projectMemoryFilePath, CancellationToken cancellationToken)
    {
        if (!projectId.HasValue || string.IsNullOrWhiteSpace(projectMemoryFilePath))
        {
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var projectMemoryService = scope.ServiceProvider.GetRequiredService<IProjectMemoryService>();
        await projectMemoryService.SyncMemoryFromFileAsync(projectId.Value, projectMemoryFilePath, cancellationToken);
    }

    private static MessageRole ParseMessageRole(string role)
    {
        return role.ToLowerInvariant() switch
        {
            "user" => MessageRole.User,
            "assistant" => MessageRole.Assistant,
            "system" => MessageRole.System,
            "thinking" => MessageRole.System,
            "reasoning" => MessageRole.System,
            "reasoning_summary" => MessageRole.System,
            "plan" => MessageRole.System,
            "tool_use" => MessageRole.ToolUse,
            "tool_result" => MessageRole.ToolResult,
            "tool_error" => MessageRole.ToolResult,
            _ => MessageRole.Assistant
        };
    }
}
