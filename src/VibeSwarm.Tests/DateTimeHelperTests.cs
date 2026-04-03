using VibeSwarm.Shared.Utilities;

namespace VibeSwarm.Tests;

public class DateTimeHelperTests : IDisposable
{
	public DateTimeHelperTests()
	{
		DateTimeHelper.ConfigureTimeZone("America/New_York");
	}

	public void Dispose()
	{
		DateTimeHelper.ConfigureTimeZone("UTC");
	}

	[Fact]
	public void FormatDateTime_UsesUSFormat_NoLeadingZeroOnHour()
	{
		var dt = new DateTime(2026, 3, 5, 14, 5, 9, DateTimeKind.Utc); // 9:05:09 AM ET

		var result = dt.FormatDateTime();

		Assert.Equal("3/5/2026 9:05:09 AM", result);
	}

	[Fact]
	public void FormatDateTimeShort_UsesUSFormat_NoLeadingZeroOnHour()
	{
		var dt = new DateTime(2026, 7, 15, 17, 30, 0, DateTimeKind.Utc); // 1:30 PM EDT

		var result = dt.FormatDateTimeShort();

		Assert.Equal("7/15/2026 1:30 PM", result);
	}

	[Fact]
	public void FormatDate_UsesUSFormat_NoLeadingZero()
	{
		var dt = new DateTime(2026, 1, 9, 12, 0, 0, DateTimeKind.Utc);

		var result = dt.FormatDate();

		Assert.Equal("1/9/2026", result);
	}

	[Fact]
	public void FormatTime_NoLeadingZeroOnHour()
	{
		var dt = new DateTime(2026, 6, 1, 15, 7, 30, DateTimeKind.Utc); // 11:07:30 AM EDT

		var result = dt.FormatTime();

		Assert.Equal("11:07:30 AM", result);
	}

	[Fact]
	public void FormatTimeShort_NoLeadingZeroOnHour()
	{
		var dt = new DateTime(2026, 1, 15, 6, 45, 0, DateTimeKind.Utc); // 1:45 AM EST

		var result = dt.FormatTimeShort();

		Assert.Equal("1:45 AM", result);
	}

	[Fact]
	public void FormatDateShort_ReturnsAbbreviatedMonthAndDay()
	{
		var dt = new DateTime(2026, 3, 25, 12, 0, 0, DateTimeKind.Utc);

		var result = dt.FormatDateShort();

		Assert.Equal("Mar 25", result);
	}

	[Fact]
	public void FormatDateShort_Nullable_ReturnsEmptyForNull()
	{
		DateTime? dt = null;

		var result = dt.FormatDateShort();

		Assert.Equal(string.Empty, result);
	}

	[Fact]
	public void FormatDateTime_Nullable_ReturnsEmptyForNull()
	{
		DateTime? dt = null;

		var result = dt.FormatDateTime();

		Assert.Equal(string.Empty, result);
	}

	[Fact]
	public void FormatDateTime_ConvertsToConfiguredTimezone()
	{
		// Midnight UTC on Jan 1 should be Dec 31 in Eastern time (EST = UTC-5)
		var dt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

		var result = dt.FormatDate();

		Assert.Equal("12/31/2025", result);
	}

	[Fact]
	public void FormatDateTime_WithUtcTimezone_NoConversion()
	{
		DateTimeHelper.ConfigureTimeZone("UTC");
		var dt = new DateTime(2026, 3, 5, 14, 30, 0, DateTimeKind.Utc);

		var result = dt.FormatDateTimeShort();

		Assert.Equal("3/5/2026 2:30 PM", result);
	}

	[Fact]
	public void FormatRelativeToNow_ReturnsPastRelativeText()
	{
		var reference = new DateTime(2026, 3, 5, 14, 30, 0, DateTimeKind.Utc);
		var dt = reference.AddMinutes(-42);

		var result = dt.FormatRelativeToNow(reference);

		Assert.Equal("42m ago", result);
	}

	[Fact]
	public void FormatRelativeToNow_ReturnsFutureRelativeText()
	{
		var reference = new DateTime(2026, 3, 5, 14, 30, 0, DateTimeKind.Utc);
		var dt = reference.AddHours(3);

		var result = dt.FormatRelativeToNow(reference);

		Assert.Equal("in 3h", result);
	}

	[Fact]
	public void FormatRelativeToNow_ReturnsJustNowForRecentPast()
	{
		var reference = new DateTime(2026, 3, 5, 14, 30, 0, DateTimeKind.Utc);
		var dt = reference.AddSeconds(-20);

		var result = dt.FormatRelativeToNow(reference);

		Assert.Equal("just now", result);
	}
}
