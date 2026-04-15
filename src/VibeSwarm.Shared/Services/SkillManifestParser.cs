using System.ComponentModel.DataAnnotations;
using VibeSwarm.Shared.Validation;

namespace VibeSwarm.Shared.Services;

/// <summary>
/// Shared parser for SKILL.md frontmatter and name normalization. All installer strategies
/// (ZIP import, marketplace, local path) route through this so they produce consistent
/// metadata and the same validation errors.
/// </summary>
public static class SkillManifestParser
{
	private const int SkillDescriptionMaxLength = ValidationLimits.SkillDescriptionMaxLength;
	private const int SkillNameMaxLength = 100;

	/// <summary>
	/// Parses YAML-ish frontmatter delimited by <c>---</c> lines at the top of a Markdown
	/// document and returns the metadata dictionary plus the body below the closing delimiter.
	/// Supports scalar values and the simple folded-scalar forms (<c>&gt;</c>, <c>|</c>).
	/// If no frontmatter is present, returns an empty metadata dict and the original content.
	/// </summary>
	public static (IReadOnlyDictionary<string, string> Metadata, string Body) ParseFrontMatter(string content)
	{
		if (string.IsNullOrEmpty(content))
		{
			return (new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), string.Empty);
		}

		var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
		if (lines.Length == 0 || lines[0].Trim() != "---")
		{
			return (new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), content);
		}

		var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var index = 1;
		while (index < lines.Length)
		{
			var line = lines[index];
			if (line.Trim() == "---")
			{
				index++;
				break;
			}

			if (string.IsNullOrWhiteSpace(line))
			{
				index++;
				continue;
			}

			var separatorIndex = line.IndexOf(':');
			if (separatorIndex <= 0)
			{
				index++;
				continue;
			}

			var key = line[..separatorIndex].Trim();
			var value = line[(separatorIndex + 1)..].Trim();

			if (value is ">" or "|" or ">-" or "|-")
			{
				index++;
				var continuation = new List<string>();
				while (index < lines.Length)
				{
					var continuationLine = lines[index];
					if (continuationLine.Trim() == "---")
					{
						break;
					}

					if (continuationLine.Length > 0 && char.IsWhiteSpace(continuationLine[0]))
					{
						continuation.Add(continuationLine.Trim());
						index++;
						continue;
					}

					break;
				}

				metadata[key] = string.Join(' ', continuation).Trim();
				continue;
			}

			metadata[key] = value.Trim().Trim('"');
			index++;
		}

		var body = string.Join('\n', lines.Skip(index)).Trim();
		return (metadata, body);
	}

	/// <summary>
	/// Returns the value for the given frontmatter key or null if absent.
	/// </summary>
	public static string? GetValue(IReadOnlyDictionary<string, string> metadata, string key)
		=> metadata.TryGetValue(key, out var value) ? value : null;

	/// <summary>
	/// Normalizes a skill name (from frontmatter or a fallback like the filename) to the
	/// hyphen-separated, lowercase, alphanumeric form used as the unique identifier.
	/// Throws <see cref="ValidationException"/> if the resulting name is empty.
	/// </summary>
	public static string NormalizeName(string? rawName, string fallbackName)
	{
		var source = string.IsNullOrWhiteSpace(rawName) ? fallbackName : rawName;
		var normalized = new string(source
			.Trim()
			.ToLowerInvariant()
			.Select(character => char.IsLetterOrDigit(character) ? character : '-')
			.ToArray());

		while (normalized.Contains("--", StringComparison.Ordinal))
		{
			normalized = normalized.Replace("--", "-", StringComparison.Ordinal);
		}

		normalized = normalized.Trim('-');
		if (string.IsNullOrWhiteSpace(normalized))
		{
			throw new ValidationException("Imported skill name is empty.");
		}

		return normalized.Length <= SkillNameMaxLength
			? normalized
			: normalized[..SkillNameMaxLength].TrimEnd('-');
	}

	/// <summary>
	/// Trims the description to the allowed maximum and records a warning when truncation
	/// happened. Returns null for empty/whitespace inputs.
	/// </summary>
	public static string? NormalizeDescription(string? description, List<string> warnings)
	{
		if (string.IsNullOrWhiteSpace(description))
		{
			return null;
		}

		var trimmed = description.Trim();
		if (trimmed.Length <= SkillDescriptionMaxLength)
		{
			return trimmed;
		}

		warnings.Add($"Description was truncated to {SkillDescriptionMaxLength} characters.");
		return trimmed[..SkillDescriptionMaxLength].TrimEnd();
	}
}
