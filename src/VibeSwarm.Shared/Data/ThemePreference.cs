namespace VibeSwarm.Shared.Data;

public enum ThemePreference
{
	System,
	Light,
	Dark
}

public static class ThemePreferenceExtensions
{
	public static string ToValue(this ThemePreference preference) => preference switch
	{
		ThemePreference.Light => "light",
		ThemePreference.Dark => "dark",
		_ => "system"
	};

	public static string ToDisplayName(this ThemePreference preference) => preference switch
	{
		ThemePreference.Light => "Light",
		ThemePreference.Dark => "Dark",
		_ => "System"
	};

	public static bool TryParse(string? value, out ThemePreference preference)
	{
		switch (value?.Trim().ToLowerInvariant())
		{
			case "light":
				preference = ThemePreference.Light;
				return true;
			case "dark":
				preference = ThemePreference.Dark;
				return true;
			case "system":
				preference = ThemePreference.System;
				return true;
			default:
				preference = ThemePreference.System;
				return false;
		}
	}

	public static ThemePreference ParseOrDefault(string? value)
	{
		return TryParse(value, out var preference)
			? preference
			: ThemePreference.System;
	}
}
