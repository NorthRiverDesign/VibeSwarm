using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.Utilities;

namespace VibeSwarm.Client.Services;

public sealed class AppTimeZoneService
{
	private readonly ISettingsService _settingsService;
	private readonly ILogger<AppTimeZoneService> _logger;
	private readonly SemaphoreSlim _initializationLock = new(1, 1);

	private IReadOnlyList<TimeZoneOption>? _offeredTimeZones;
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

	/// <summary>
	/// The zones the app offers: the main US zones, east to west, then UTC, the fallback when
	/// none is set. A saved zone outside that list is kept as the last option, so opening
	/// Settings never changes it silently.
	/// </summary>
	public IReadOnlyList<TimeZoneOption> GetTimeZoneOptions(string? savedTimeZoneId = null)
	{
		_offeredTimeZones ??= OfferedTimeZones
			.Select(offered => (offered.Id, offered.Name, Zone: DateTimeHelper.ResolveTimeZone(offered.Id)))
			// A zone missing from this machine's time zone data resolves to UTC; skip it.
			.Where(offered => offered.Id == DateTimeHelper.UtcTimeZoneId || offered.Zone != TimeZoneInfo.Utc)
			.Select(offered => new TimeZoneOption(offered.Zone, offered.Id == DateTimeHelper.UtcTimeZoneId
				? offered.Name
				: $"{offered.Name} ({DateTimeHelper.FormatUtcOffset(offered.Zone)})"))
			.ToList();

		var saved = DateTimeHelper.ResolveTimeZone(savedTimeZoneId);
		return _offeredTimeZones.Any(option => option.Zone.Id == saved.Id)
			? _offeredTimeZones
			: [.. _offeredTimeZones, new TimeZoneOption(saved, DateTimeHelper.GetTimeZoneOptionLabel(saved))];
	}

	private static readonly (string Id, string Name)[] OfferedTimeZones =
	[
		("America/New_York", "Eastern"),
		("America/Chicago", "Central"),
		("America/Denver", "Mountain"),
		("America/Phoenix", "Arizona"),
		("America/Los_Angeles", "Pacific"),
		("America/Anchorage", "Alaska"),
		("Pacific/Honolulu", "Hawaii"),
		(DateTimeHelper.UtcTimeZoneId, "UTC")
	];
}

public sealed record TimeZoneOption(TimeZoneInfo Zone, string Label);
