using System.Collections.ObjectModel;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.Utilities;

namespace VibeSwarm.Client.Services;

public sealed class AppTimeZoneService
{
	private readonly ISettingsService _settingsService;
	private readonly ILogger<AppTimeZoneService> _logger;
	private readonly SemaphoreSlim _initializationLock = new(1, 1);

	private IReadOnlyList<TimeZoneInfo>? _availableTimeZones;
	private bool _isInitialized;

	public AppTimeZoneService(ISettingsService settingsService, ILogger<AppTimeZoneService> logger)
	{
		_settingsService = settingsService;
		_logger = logger;
	}

	public string CurrentTimeZoneId => DateTimeHelper.CurrentTimeZoneId;

	public async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
	{
		if (_isInitialized)
		{
			return;
		}

		await _initializationLock.WaitAsync(cancellationToken);
		try
		{
			if (_isInitialized)
			{
				return;
			}

			string? configuredTimeZoneId = null;
			try
			{
				var settings = await _settingsService.GetSettingsAsync(cancellationToken);
				configuredTimeZoneId = settings.TimeZoneId;
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Failed to load configured timezone. Falling back to UTC.");
			}

			ApplyTimeZone(configuredTimeZoneId);
		}
		finally
		{
			_initializationLock.Release();
		}
	}

	public void ApplyTimeZone(string? timeZoneId)
	{
		DateTimeHelper.ConfigureTimeZone(timeZoneId);
		_isInitialized = true;
	}

	public IReadOnlyList<TimeZoneInfo> GetAvailableTimeZones()
	{
		if (_availableTimeZones != null)
		{
			return _availableTimeZones;
		}

		var timeZones = TimeZoneInfo.GetSystemTimeZones()
			.OrderBy(zone => zone.BaseUtcOffset)
			.ThenBy(zone => zone.Id, StringComparer.Ordinal)
			.ToList();

		if (!timeZones.Any(zone => string.Equals(zone.Id, TimeZoneInfo.Utc.Id, StringComparison.OrdinalIgnoreCase)))
		{
			timeZones.Insert(0, TimeZoneInfo.Utc);
		}

		_availableTimeZones = new ReadOnlyCollection<TimeZoneInfo>(timeZones);
		return _availableTimeZones;
	}
}
