using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Services;

/// <summary>
/// Background service that monitors Ideas processing for all projects.
/// When a project has Ideas auto-processing enabled, this service will
/// process the next idea once the previous job completes.
/// </summary>
public class IdeasProcessingService : BackgroundService
{
	private readonly IServiceScopeFactory _scopeFactory;
	private readonly ILogger<IdeasProcessingService> _logger;
	private readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(10);
	private const int RecoveryIntervalCycles = 6; // run recovery every ~60 seconds
	private int _cycleCount;

	public IdeasProcessingService(
		IServiceScopeFactory scopeFactory,
		ILogger<IdeasProcessingService> logger)
	{
		_scopeFactory = scopeFactory;
		_logger = logger;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		_logger.LogInformation("Ideas Processing Service started");

		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				// Periodically scan for ideas stuck in IsProcessing=true with a terminal or missing job
				_cycleCount++;
				if (_cycleCount >= RecoveryIntervalCycles)
				{
					_cycleCount = 0;
					await RecoverStuckIdeasAsync(stoppingToken);
				}

				await ProcessActiveProjectsAsync(stoppingToken);
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				break;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error in Ideas processing monitor");
			}

			try
			{
				await Task.Delay(_checkInterval, stoppingToken);
			}
			catch (OperationCanceledException)
			{
				break;
			}
		}

		_logger.LogInformation("Ideas Processing Service stopped");
	}

	private async Task RecoverStuckIdeasAsync(CancellationToken cancellationToken)
	{
		try
		{
			using var scope = _scopeFactory.CreateScope();
			var ideaService = scope.ServiceProvider.GetRequiredService<IIdeaService>();
			await ideaService.RecoverStuckIdeasAsync(cancellationToken);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Error recovering stuck ideas");
		}
	}

	private async Task ProcessActiveProjectsAsync(CancellationToken cancellationToken)
	{
		using var scope = _scopeFactory.CreateScope();
		var ideaService = scope.ServiceProvider.GetRequiredService<IIdeaService>();

		// Get all projects with active Ideas processing
		var activeProjectIds = await ideaService.GetActiveProcessingProjectsAsync(cancellationToken);

		foreach (var projectId in activeProjectIds)
		{
			try
			{
				// Check if we can process the next idea
				var processed = await ideaService.ProcessNextIdeaIfReadyAsync(projectId, cancellationToken);

				if (processed)
				{
					_logger.LogInformation("Processed next idea for project {ProjectId}", projectId);
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error processing ideas for project {ProjectId}", projectId);
			}
		}
	}
}
