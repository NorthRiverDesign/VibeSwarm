using System.ComponentModel.DataAnnotations;

namespace VibeSwarm.Shared.Validation;

/// <summary>
/// Accepts any absolute URL, whatever its scheme. Unlike <see cref="UrlAttribute"/>, which allows
/// only http, https and ftp, this lets an environment point at mysql://, postgres:// or ssh://.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class AbsoluteUrlAttribute : DataTypeAttribute
{
	public AbsoluteUrlAttribute()
		: base(DataType.Url)
	{
		ErrorMessage = "The {0} field must be an absolute URL.";
	}

	public override bool IsValid(object? value)
	{
		return value switch
		{
			null => true,
			string text => Uri.TryCreate(text.Trim(), UriKind.Absolute, out _),
			_ => false
		};
	}
}
