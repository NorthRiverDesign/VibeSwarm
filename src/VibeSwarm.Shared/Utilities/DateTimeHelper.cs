namespace VibeSwarm.Shared.Utilities;

/// <summary>
/// Helper class for consistent date/time formatting across the application
/// </summary>
public static class DateTimeHelper
{
	/// <summary>
	/// Eastern Standard Time zone info
	/// </summary>
	private static readonly TimeZoneInfo EasternTimeZone = GetEasternTimeZone();

	private static TimeZoneInfo GetEasternTimeZone()
	{
		try
		{
			// Windows uses "Eastern Standard Time"
			return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
		}
		catch
		{
			try
			{
				// Linux/macOS use IANA time zone IDs
				return TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
			}
			catch
			{
				// Fallback to UTC-5 if neither works
				return TimeZoneInfo.CreateCustomTimeZone("EST", TimeSpan.FromHours(-5), "Eastern Standard Time", "Eastern Standard Time");
			}
		}
	}

	/// <summary>
	/// Converts a UTC DateTime to Eastern time
	/// </summary>
	public static DateTime ToEasternTime(this DateTime utcDateTime)
	{
		if (utcDateTime.Kind == DateTimeKind.Local)
		{
			utcDateTime = utcDateTime.ToUniversalTime();
		}
		return TimeZoneInfo.ConvertTimeFromUtc(utcDateTime, EasternTimeZone);
	}

	/// <summary>
	/// Formats a DateTime as MM/DD/YYYY hh:mm:ss tt (12-hour with AM/PM)
	/// </summary>
	public static string FormatDateTime(this DateTime dateTime)
	{
		return dateTime.ToEasternTime().ToString("MM/dd/yyyy hh:mm:ss tt");
	}

	/// <summary>
	/// Formats a DateTime as MM/DD/YYYY hh:mm tt (12-hour with AM/PM, no seconds)
	/// </summary>
	public static string FormatDateTimeShort(this DateTime dateTime)
	{
		return dateTime.ToEasternTime().ToString("MM/dd/yyyy hh:mm tt");
	}

	/// <summary>
	/// Formats a DateTime as MM/DD/YYYY
	/// </summary>
	public static string FormatDate(this DateTime dateTime)
	{
		return dateTime.ToEasternTime().ToString("MM/dd/yyyy");
	}

	/// <summary>
	/// Formats a DateTime as hh:mm:ss tt (12-hour with AM/PM)
	/// </summary>
	public static string FormatTime(this DateTime dateTime)
	{
		return dateTime.ToEasternTime().ToString("hh:mm:ss tt");
	}

	/// <summary>
	/// Formats a DateTime as hh:mm tt (12-hour with AM/PM, no seconds)
	/// </summary>
	public static string FormatTimeShort(this DateTime dateTime)
	{
		return dateTime.ToEasternTime().ToString("hh:mm tt");
	}

	/// <summary>
	/// Formats a nullable DateTime, returns empty string if null
	/// </summary>
	public static string FormatDateTime(this DateTime? dateTime)
	{
		return dateTime?.FormatDateTime() ?? string.Empty;
	}

	/// <summary>
	/// Formats a nullable DateTime short format, returns empty string if null
	/// </summary>
	public static string FormatDateTimeShort(this DateTime? dateTime)
	{
		return dateTime?.FormatDateTimeShort() ?? string.Empty;
	}

	/// <summary>
	/// Formats a nullable Date, returns empty string if null
	/// </summary>
	public static string FormatDate(this DateTime? dateTime)
	{
		return dateTime?.FormatDate() ?? string.Empty;
	}

	/// <summary>
	/// Formats a nullable Time, returns empty string if null
	/// </summary>
	public static string FormatTime(this DateTime? dateTime)
	{
		return dateTime?.FormatTime() ?? string.Empty;
	}

	/// <summary>
	/// Formats a nullable Time short format, returns empty string if null
	/// </summary>
	public static string FormatTimeShort(this DateTime? dateTime)
	{
		return dateTime?.FormatTimeShort() ?? string.Empty;
	}
}
