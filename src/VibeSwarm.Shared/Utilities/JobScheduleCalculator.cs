using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Utilities;

public static class JobScheduleCalculator
{
	public static DateTime CalculateNextRunUtc(JobSchedule schedule, DateTime afterUtc)
		=> CalculateNextRunUtc(schedule, afterUtc, TimeZoneInfo.Utc);

	public static DateTime CalculateNextRunUtc(JobSchedule schedule, DateTime afterUtc, TimeZoneInfo timeZone)
	{
		afterUtc = NormalizeUtc(afterUtc);
		var localAfter = TimeZoneInfo.ConvertTimeFromUtc(afterUtc, timeZone);

		return schedule.Frequency switch
		{
			JobScheduleFrequency.Minutes => CalculateNextMinutesRun(schedule, afterUtc),
			JobScheduleFrequency.Hourly => CalculateNextHourlyRun(schedule, afterUtc, localAfter, timeZone),
			JobScheduleFrequency.Weekly => CalculateNextWeeklyRun(schedule, afterUtc, localAfter, timeZone),
			JobScheduleFrequency.Monthly => CalculateNextMonthlyRun(schedule, afterUtc, localAfter, timeZone),
			_ => CalculateNextDailyRun(schedule, afterUtc, localAfter, timeZone)
		};
	}

	private static DateTime CalculateNextMinutesRun(JobSchedule schedule, DateTime afterUtc)
	{
		var interval = Math.Clamp(schedule.IntervalMinutes, 5, 60);
		return afterUtc.AddMinutes(interval);
	}

	private static DateTime CalculateNextHourlyRun(JobSchedule schedule, DateTime afterUtc, DateTime localAfter, TimeZoneInfo timeZone)
	{
		var candidateLocal = BuildLocal(localAfter.Year, localAfter.Month, localAfter.Day, localAfter.Hour, schedule.MinuteUtc);
		var candidateUtc = GetNextUtcCandidate(candidateLocal, timeZone, afterUtc);
		if (candidateUtc > afterUtc)
		{
			return candidateUtc;
		}

		return GetFirstUtcCandidate(candidateLocal.AddHours(1), timeZone);
	}

	private static DateTime CalculateNextDailyRun(JobSchedule schedule, DateTime afterUtc, DateTime localAfter, TimeZoneInfo timeZone)
	{
		var candidateLocal = BuildLocal(localAfter.Year, localAfter.Month, localAfter.Day, schedule.HourUtc, schedule.MinuteUtc);
		var candidateUtc = GetNextUtcCandidate(candidateLocal, timeZone, afterUtc);
		if (candidateUtc > afterUtc)
		{
			return candidateUtc;
		}

		return GetFirstUtcCandidate(candidateLocal.AddDays(1), timeZone);
	}

	private static DateTime CalculateNextWeeklyRun(JobSchedule schedule, DateTime afterUtc, DateTime localAfter, TimeZoneInfo timeZone)
	{
		var dayDifference = ((int)schedule.WeeklyDay - (int)localAfter.DayOfWeek + 7) % 7;
		var candidateDate = localAfter.Date.AddDays(dayDifference);
		var candidateLocal = BuildLocal(candidateDate.Year, candidateDate.Month, candidateDate.Day, schedule.HourUtc, schedule.MinuteUtc);
		var candidateUtc = GetNextUtcCandidate(candidateLocal, timeZone, afterUtc);
		if (candidateUtc > afterUtc)
		{
			return candidateUtc;
		}

		return GetFirstUtcCandidate(candidateLocal.AddDays(7), timeZone);
	}

	private static DateTime CalculateNextMonthlyRun(JobSchedule schedule, DateTime afterUtc, DateTime localAfter, TimeZoneInfo timeZone)
	{
		var candidateLocal = BuildMonthlyCandidate(localAfter.Year, localAfter.Month, schedule);
		var candidateUtc = GetNextUtcCandidate(candidateLocal, timeZone, afterUtc);
		if (candidateUtc > afterUtc)
		{
			return candidateUtc;
		}

		var nextMonth = new DateTime(localAfter.Year, localAfter.Month, 1).AddMonths(1);
		return GetFirstUtcCandidate(BuildMonthlyCandidate(nextMonth.Year, nextMonth.Month, schedule), timeZone);
	}

	private static DateTime BuildMonthlyCandidate(int year, int month, JobSchedule schedule)
	{
		var day = Math.Min(schedule.DayOfMonth, DateTime.DaysInMonth(year, month));
		return BuildLocal(year, month, day, schedule.HourUtc, schedule.MinuteUtc);
	}

	private static DateTime BuildLocal(int year, int month, int day, int hour, int minute)
		=> new(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);

	private static DateTime GetNextUtcCandidate(DateTime localCandidate, TimeZoneInfo timeZone, DateTime afterUtc)
		=> GetUtcCandidates(localCandidate, timeZone).FirstOrDefault(candidate => candidate > afterUtc);

	private static DateTime GetFirstUtcCandidate(DateTime localCandidate, TimeZoneInfo timeZone)
		=> GetUtcCandidates(localCandidate, timeZone)[0];

	private static IReadOnlyList<DateTime> GetUtcCandidates(DateTime localCandidate, TimeZoneInfo timeZone)
	{
		localCandidate = DateTime.SpecifyKind(localCandidate, DateTimeKind.Unspecified);

		if (timeZone.IsInvalidTime(localCandidate))
		{
			var adjustedLocalCandidate = localCandidate;
			for (var minute = 0; minute < 180 && timeZone.IsInvalidTime(adjustedLocalCandidate); minute++)
			{
				adjustedLocalCandidate = adjustedLocalCandidate.AddMinutes(1);
			}

			return [TimeZoneInfo.ConvertTimeToUtc(adjustedLocalCandidate, timeZone)];
		}

		if (timeZone.IsAmbiguousTime(localCandidate))
		{
			return timeZone.GetAmbiguousTimeOffsets(localCandidate)
				.Select(offset => new DateTimeOffset(localCandidate, offset).UtcDateTime)
				.OrderBy(candidate => candidate)
				.ToArray();
		}

		return [TimeZoneInfo.ConvertTimeToUtc(localCandidate, timeZone)];
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
