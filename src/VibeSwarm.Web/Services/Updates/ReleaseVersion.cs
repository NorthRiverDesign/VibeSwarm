using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace VibeSwarm.Web.Services.Updates;

/// <summary>
/// A semantic version (<c>1.4.2</c>, <c>v1.5.0-beta.2</c>) as release tags and stamped builds carry
/// it. Build metadata after <c>+</c> is dropped; a pre-release sorts before its release.
/// </summary>
public sealed record ReleaseVersion(int Major, int Minor, int Patch, string? Prerelease) : IComparable<ReleaseVersion>
{
	/// <summary>The label builds get when release.yml didn't stamp them from a tag.</summary>
	public const string DevelopmentLabel = "dev";

	public bool IsPrerelease => !string.IsNullOrEmpty(Prerelease);

	public bool IsDevelopmentBuild => Major == 0 && Minor == 0 && Patch == 0 &&
		string.Equals(Prerelease, DevelopmentLabel, StringComparison.OrdinalIgnoreCase);

	public static bool TryParse(string? text, [NotNullWhen(true)] out ReleaseVersion? version)
	{
		version = null;
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}

		var value = text.Trim();
		if (value.StartsWith('v') || value.StartsWith('V'))
		{
			value = value[1..];
		}

		var plus = value.IndexOf('+');
		if (plus >= 0)
		{
			value = value[..plus];
		}

		string? prerelease = null;
		var dash = value.IndexOf('-');
		if (dash >= 0)
		{
			prerelease = value[(dash + 1)..];
			value = value[..dash];
			if (prerelease.Length == 0 || !prerelease.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-'))
			{
				return false;
			}
		}

		var parts = value.Split('.');
		if (parts.Length is < 2 or > 3)
		{
			return false;
		}

		var numbers = new int[3];
		for (var i = 0; i < parts.Length; i++)
		{
			if (parts[i].Length == 0 || !parts[i].All(char.IsAsciiDigit) ||
				!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
			{
				return false;
			}
		}

		version = new ReleaseVersion(numbers[0], numbers[1], numbers[2], prerelease);
		return true;
	}

	public int CompareTo(ReleaseVersion? other)
	{
		if (other is null)
		{
			return 1;
		}

		var result = Major.CompareTo(other.Major);
		if (result == 0) result = Minor.CompareTo(other.Minor);
		if (result == 0) result = Patch.CompareTo(other.Patch);
		if (result != 0)
		{
			return result;
		}

		return ComparePrerelease(Prerelease, other.Prerelease);
	}

	public static bool operator >(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) > 0;
	public static bool operator <(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) < 0;
	public static bool operator >=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) >= 0;
	public static bool operator <=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) <= 0;

	public override string ToString() => IsPrerelease
		? $"{Major}.{Minor}.{Patch}-{Prerelease}"
		: $"{Major}.{Minor}.{Patch}";

	// SemVer 2.0 precedence: no label beats any label; dot-separated identifiers compare numerically
	// when both are numbers, otherwise ordinally, and a shorter list of equal identifiers sorts first.
	private static int ComparePrerelease(string? left, string? right)
	{
		if (string.IsNullOrEmpty(left)) return string.IsNullOrEmpty(right) ? 0 : 1;
		if (string.IsNullOrEmpty(right)) return -1;

		var leftParts = left.Split('.');
		var rightParts = right.Split('.');
		for (var i = 0; i < Math.Min(leftParts.Length, rightParts.Length); i++)
		{
			var leftIsNumber = long.TryParse(leftParts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber);
			var rightIsNumber = long.TryParse(rightParts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber);
			var result = (leftIsNumber, rightIsNumber) switch
			{
				(true, true) => leftNumber.CompareTo(rightNumber),
				(true, false) => -1,
				(false, true) => 1,
				_ => string.CompareOrdinal(leftParts[i], rightParts[i])
			};
			if (result != 0)
			{
				return result;
			}
		}

		return leftParts.Length.CompareTo(rightParts.Length);
	}
}
