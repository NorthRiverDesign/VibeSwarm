using System.Globalization;

namespace VibeSwarm.Shared.Utilities;

public static class DateTimeHelper
{
	public const string UtcTimeZoneId = "UTC";
	private static readonly CultureInfo USCulture = CultureInfo.GetCultureInfo("en-US");

	private static readonly object TimeZoneLock = new();
	private static TimeZoneInfo _currentTimeZone = TimeZoneInfo.Utc;
	private static string _currentTimeZoneId = UtcTimeZoneId;

	public static string CurrentTimeZoneId
	{
		get
		{
			lock (TimeZoneLock)
			{
				return _currentTimeZoneId;
			}
		}
	}

	public static TimeZoneInfo CurrentTimeZone
	{
		get
		{
			lock (TimeZoneLock)
			{
				return _currentTimeZone;
			}
		}
	}

	public static TimeZoneInfo ConfigureTimeZone(string? timeZoneId)
	{
		var timeZone = ResolveTimeZone(timeZoneId);

		lock (TimeZoneLock)
		{
			_currentTimeZone = timeZone;
			_currentTimeZoneId = timeZone.Id;
		}

		return timeZone;
	}

	public static TimeZoneInfo ResolveTimeZone(string? timeZoneId)
	{
		if (string.IsNullOrWhiteSpace(timeZoneId) || string.Equals(timeZoneId, UtcTimeZoneId, StringComparison.OrdinalIgnoreCase))
		{
			return TimeZoneInfo.Utc;
		}

		var trimmedId = timeZoneId.Trim();

		if (TryFindTimeZone(trimmedId, out var resolved))
		{
			return resolved;
		}

		if (TimeZoneInfo.TryConvertWindowsIdToIanaId(trimmedId, out var ianaId) && TryFindTimeZone(ianaId, out resolved))
		{
			return resolved;
		}

		if (TimeZoneInfo.TryConvertIanaIdToWindowsId(trimmedId, out var windowsId) && TryFindTimeZone(windowsId, out resolved))
		{
			return resolved;
		}

		return TimeZoneInfo.Utc;
	}

	public static DateTime ToConfiguredTime(this DateTime dateTime)
		=> TimeZoneInfo.ConvertTimeFromUtc(NormalizeUtc(dateTime), CurrentTimeZone);

	public static string FormatDateTimeWithZone(this DateTime dateTime)
		=> $"{dateTime.ToConfiguredTime():yyyy-MM-dd HH:mm} {CurrentTimeZoneId}";

	public static string FormatDateTime(this DateTime dateTime)
	{
		return dateTime.ToConfiguredTime().ToString("M/d/yyyy h:mm:ss tt", USCulture);
	}

	public static string FormatDateTimeShort(this DateTime dateTime)
	{
		return dateTime.ToConfiguredTime().ToString("M/d/yyyy h:mm tt", USCulture);
	}

	public static string FormatDate(this DateTime dateTime)
	{
		return dateTime.ToConfiguredTime().ToString("M/d/yyyy", USCulture);
	}

	public static string FormatTime(this DateTime dateTime)
	{
		return dateTime.ToConfiguredTime().ToString("h:mm:ss tt", USCulture);
	}

	public static string FormatTimeShort(this DateTime dateTime)
	{
		return dateTime.ToConfiguredTime().ToString("h:mm tt", USCulture);
	}

	public static string FormatDateShort(this DateTime dateTime)
	{
		return dateTime.ToConfiguredTime().ToString("MMM d", USCulture);
	}

	public static string FormatDateShort(this DateTime? dateTime)
	{
		return dateTime?.FormatDateShort() ?? string.Empty;
	}

	public static string GetTimeZoneOptionLabel(TimeZoneInfo timeZone)
	{
		var offset = timeZone.GetUtcOffset(DateTime.UtcNow);
		var sign = offset < TimeSpan.Zero ? "-" : "+";
		var absoluteOffset = offset.Duration();
		return $"(UTC{sign}{absoluteOffset.Hours:D2}:{absoluteOffset.Minutes:D2}) {timeZone.Id}";
	}

	public static string FormatDateTime(this DateTime? dateTime)
	{
		return dateTime?.FormatDateTime() ?? string.Empty;
	}

	public static string FormatDateTimeShort(this DateTime? dateTime)
	{
		return dateTime?.FormatDateTimeShort() ?? string.Empty;
	}

	public static string FormatDate(this DateTime? dateTime)
	{
		return dateTime?.FormatDate() ?? string.Empty;
	}

	public static string FormatTime(this DateTime? dateTime)
	{
		return dateTime?.FormatTime() ?? string.Empty;
	}

	public static string FormatTimeShort(this DateTime? dateTime)
	{
		return dateTime?.FormatTimeShort() ?? string.Empty;
	}

	private static bool TryFindTimeZone(string timeZoneId, out TimeZoneInfo timeZone)
	{
		try
		{
			timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
			return true;
		}
		catch (TimeZoneNotFoundException)
		{
		}
		catch (InvalidTimeZoneException)
		{
		}

		timeZone = TimeZoneInfo.Utc;
		return false;
	}

	private static DateTime NormalizeUtc(DateTime value)
	{
		if (value.Kind == DateTimeKind.Utc)
		{
			return value;
		}

		if (value.Kind == DateTimeKind.Local)
		{
			return value.ToUniversalTime();
		}

		return DateTime.SpecifyKind(value, DateTimeKind.Utc);
	}
}
