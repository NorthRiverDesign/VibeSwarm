using System.Globalization;
using System.Text.RegularExpressions;

namespace VibeSwarm.Tests;

/// <summary>
/// Holds the palette in section 1 of site.css to WCAG contrast in both themes: text 4.5:1 on
/// every surface it sits on, button labels 4.5:1 on their fill, chart and meter fills 3:1.
/// </summary>
public sealed class ThemeContrastTests
{
	private const int Light = 0;
	private const int Dark = 1;

	private static readonly string[] TextSurfaces = ["--bs-bg-body", "--bs-bg-1", "--bs-bg-2"];

	// bg-2 is the meter track (Bootstrap's --bs-progress-bg) and bg-3 the hovered panel.
	private static readonly string[] DataSurfaces = ["--bs-bg-body", "--bs-bg-1", "--bs-bg-2", "--bs-bg-3"];

	public static TheoryData<string> TextTokens => new()
	{
		"--bs-fg-body",
		"--bs-fg-1",
		"--bs-fg-2",
		"--bs-secondary-fg",
		"--bs-link-color",
		"--bs-primary-fg",
		"--bs-success-fg",
		"--bs-warning-fg",
		"--bs-danger-fg",
		"--bs-info-fg",
	};

	public static TheoryData<string> ThemeNames => new() { "primary", "success", "warning", "danger", "info" };

	public static TheoryData<string> DataTokens => new()
	{
		"--vs-data-primary",
		"--vs-data-success",
		"--vs-data-warning",
		"--vs-data-danger",
		"--vs-data-info",
		"--vs-data-violet",
	};

	[Theory]
	[MemberData(nameof(TextTokens))]
	public void TextColours_ReachFourAndAHalfToOne_OnEverySurface(string token)
	{
		var tokens = ReadRootTokens();

		foreach (var theme in new[] { Light, Dark })
		{
			foreach (var surface in TextSurfaces)
			{
				var ratio = Contrast(Resolve(tokens, token, theme), Resolve(tokens, surface, theme));
				Assert.True(ratio >= 4.5, $"{token} on {surface} in the {ThemeName(theme)} theme is {ratio:F2}:1, below 4.5:1.");
			}
		}
	}

	[Theory]
	[MemberData(nameof(ThemeNames))]
	public void ButtonLabels_ReachFourAndAHalfToOne_OnTheirFill(string name)
	{
		var tokens = ReadRootTokens();

		foreach (var theme in new[] { Light, Dark })
		{
			var ratio = Contrast(Resolve(tokens, $"--bs-{name}-contrast", theme), Resolve(tokens, $"--bs-{name}-bg", theme));
			Assert.True(ratio >= 4.5, $"--bs-{name}-contrast on --bs-{name}-bg in the {ThemeName(theme)} theme is {ratio:F2}:1, below 4.5:1.");
		}
	}

	[Theory]
	[MemberData(nameof(DataTokens))]
	public void ChartAndMeterFills_ReachThreeToOne_OnEverySurface(string token)
	{
		var tokens = ReadRootTokens();

		foreach (var theme in new[] { Light, Dark })
		{
			foreach (var surface in DataSurfaces)
			{
				var ratio = Contrast(Resolve(tokens, token, theme), Resolve(tokens, surface, theme));
				Assert.True(ratio >= 3, $"{token} on {surface} in the {ThemeName(theme)} theme is {ratio:F2}:1, below 3:1.");
			}
		}
	}

	[Fact]
	public void SiteCss_MixesThemeTintsWithoutRotatingTheirHue()
	{
		// Mixing in oklch takes the short way round the hue wheel to the near-neutral body
		// colour, which turned warning emphasis text red and danger emphasis text magenta.
		Assert.DoesNotContain("in oklch", ReadSiteCss());
	}

	private static string ThemeName(int theme) => theme == Light ? "light" : "dark";

	/// <summary>The custom properties of section 1's <c>:root, :host</c> block.</summary>
	private static Dictionary<string, string> ReadRootTokens()
	{
		var css = ReadSiteCss();
		var block = Regex.Match(css, @":root,\s*:host\s*\{(?<body>[^}]*)\}");
		Assert.True(block.Success, "site.css has no :root, :host token block.");

		return Regex.Matches(block.Groups["body"].Value, @"(?<name>--[\w-]+)\s*:\s*(?<value>[^;]+);")
			.ToDictionary(match => match.Groups["name"].Value, match => match.Groups["value"].Value.Trim());
	}

	/// <summary>Follows var() and light-dark() down to a hex colour for one theme.</summary>
	private static (double R, double G, double B) Resolve(Dictionary<string, string> tokens, string name, int theme)
	{
		Assert.True(tokens.TryGetValue(name, out var value), $"site.css does not define {name}.");
		return ResolveValue(tokens, name, value!, theme);
	}

	private static (double R, double G, double B) ResolveValue(Dictionary<string, string> tokens, string name, string value, int theme)
	{
		value = value.Trim();

		if (value.StartsWith("light-dark(", StringComparison.Ordinal) && value.EndsWith(')'))
		{
			var pair = value["light-dark(".Length..^1].Split(',');
			Assert.True(pair.Length == 2, $"{name} has an unexpected light-dark() value: {value}");
			return ResolveValue(tokens, name, pair[theme], theme);
		}

		var reference = Regex.Match(value, @"^var\((?<name>--[\w-]+)\)$");
		if (reference.Success)
		{
			return Resolve(tokens, reference.Groups["name"].Value, theme);
		}

		var hex = Regex.Match(value, @"^#(?<hex>[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$");
		Assert.True(hex.Success, $"{name} must be a hex colour, var() or light-dark() for this test to check it, not {value}.");

		var digits = hex.Groups["hex"].Value;
		if (digits.Length == 3)
		{
			digits = string.Concat(digits.Select(digit => $"{digit}{digit}"));
		}

		return (
			int.Parse(digits[..2], NumberStyles.HexNumber),
			int.Parse(digits[2..4], NumberStyles.HexNumber),
			int.Parse(digits[4..6], NumberStyles.HexNumber));
	}

	private static double Contrast((double R, double G, double B) first, (double R, double G, double B) second)
	{
		var a = Luminance(first);
		var b = Luminance(second);
		return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
	}

	private static double Luminance((double R, double G, double B) colour)
	{
		static double Channel(double value)
		{
			var c = value / 255;
			return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
		}

		return (0.2126 * Channel(colour.R)) + (0.7152 * Channel(colour.G)) + (0.0722 * Channel(colour.B));
	}

	private static string ReadSiteCss()
		=> File.ReadAllText(GetRepositoryPath("src", "VibeSwarm.Client", "wwwroot", "css", "site.css"));

	private static string GetRepositoryPath(params string[] segments)
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);

		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VibeSwarm.sln")))
		{
			directory = directory.Parent;
		}

		Assert.NotNull(directory);
		return Path.Combine([directory!.FullName, .. segments]);
	}
}
