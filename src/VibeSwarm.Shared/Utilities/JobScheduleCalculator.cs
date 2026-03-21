using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Utilities;

public static class JobScheduleCalculator
{
	public static DateTime CalculateNextRunUtc(JobSchedule schedule, DateTime afterUtc)
	{
		afterUtc = NormalizeUtc(afterUtc);

		return schedule.Frequency switch
		{
			JobScheduleFrequency.Hourly => CalculateNextHourlyRun(schedule, afterUtc),
			JobScheduleFrequency.Weekly => CalculateNextWeeklyRun(schedule, afterUtc),
			JobScheduleFrequency.Monthly => CalculateNextMonthlyRun(schedule, afterUtc),
			_ => CalculateNextDailyRun(schedule, afterUtc)
		};
	}

	private static DateTime CalculateNextHourlyRun(JobSchedule schedule, DateTime afterUtc)
	{
		var candidate = new DateTime(afterUtc.Year, afterUtc.Month, afterUtc.Day, afterUtc.Hour, schedule.MinuteUtc, 0, DateTimeKind.Utc);
		if (candidate <= afterUtc)
		{
			candidate = candidate.AddHours(1);
		}

		return candidate;
	}

	private static DateTime CalculateNextDailyRun(JobSchedule schedule, DateTime afterUtc)
	{
		var candidate = BuildUtc(afterUtc.Year, afterUtc.Month, afterUtc.Day, schedule.HourUtc, schedule.MinuteUtc);
		if (candidate <= afterUtc)
		{
			candidate = candidate.AddDays(1);
		}

		return candidate;
	}

	private static DateTime CalculateNextWeeklyRun(JobSchedule schedule, DateTime afterUtc)
	{
		var dayDifference = ((int)schedule.WeeklyDay - (int)afterUtc.DayOfWeek + 7) % 7;
		var candidateDate = afterUtc.Date.AddDays(dayDifference);
		var candidate = BuildUtc(candidateDate.Year, candidateDate.Month, candidateDate.Day, schedule.HourUtc, schedule.MinuteUtc);
		if (candidate <= afterUtc)
		{
			candidate = candidate.AddDays(7);
		}

		return candidate;
	}

	private static DateTime CalculateNextMonthlyRun(JobSchedule schedule, DateTime afterUtc)
	{
		var candidate = BuildMonthlyCandidate(afterUtc.Year, afterUtc.Month, schedule);
		if (candidate <= afterUtc)
		{
			var nextMonth = new DateTime(afterUtc.Year, afterUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);
			candidate = BuildMonthlyCandidate(nextMonth.Year, nextMonth.Month, schedule);
		}

		return candidate;
	}

	private static DateTime BuildMonthlyCandidate(int year, int month, JobSchedule schedule)
	{
		var day = Math.Min(schedule.DayOfMonth, DateTime.DaysInMonth(year, month));
		return BuildUtc(year, month, day, schedule.HourUtc, schedule.MinuteUtc);
	}

	private static DateTime BuildUtc(int year, int month, int day, int hour, int minute)
		=> new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

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
