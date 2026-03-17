using System.ComponentModel.DataAnnotations;
using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Models;

public sealed class ThemePreferenceDto
{
	public string Theme { get; set; } = ThemePreference.System.ToValue();
}

public sealed class UpdateThemePreferenceRequest
{
	[Required]
	public string Theme { get; set; } = ThemePreference.System.ToValue();
}
