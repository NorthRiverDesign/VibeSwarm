using System.Buffers;

namespace VibeSwarm.Client.Components.Chat;

/// <summary>A tool's output as the transcript shows it: the images it returned, such as a
/// screenshot, as pictures, and the rest as text.</summary>
public sealed record ToolOutputContent(string Text, IReadOnlyList<string> Images)
{
	private const string Base64Marker = ";base64,";

	private static readonly string[] ImageMediaTypes = ["image/png", "image/jpeg", "image/gif", "image/webp"];

	private static readonly SearchValues<char> Base64Chars
		= SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/=");

	/// <summary>The data URL for base64 image data, or null when it isn't a raster image or is empty.</summary>
	public static string? ToImageDataUrl(string? mediaType, string? base64Data)
		=> !string.IsNullOrEmpty(base64Data) && IsImageMediaType(mediaType)
			? $"data:{mediaType!.ToLowerInvariant()}{Base64Marker}{base64Data}"
			: null;

	/// <summary>Takes each line that is an image data URL (the transcript writes one per image)
	/// out of the output; the remaining lines are the text.</summary>
	public static ToolOutputContent Parse(string? output)
	{
		if (string.IsNullOrEmpty(output) || !output.Contains(Base64Marker, StringComparison.Ordinal))
		{
			return new ToolOutputContent(output ?? string.Empty, []);
		}

		var images = new List<string>();
		var textLines = new List<string>();
		foreach (var line in output.Split('\n'))
		{
			var trimmed = line.Trim();
			if (IsImageDataUrl(trimmed))
			{
				images.Add(trimmed);
			}
			else
			{
				textLines.Add(line);
			}
		}

		return images.Count == 0
			? new ToolOutputContent(output, [])
			: new ToolOutputContent(string.Join('\n', textLines).Trim(), images);
	}

	private static bool IsImageDataUrl(string line)
	{
		if (!line.StartsWith("data:", StringComparison.Ordinal))
		{
			return false;
		}

		var marker = line.IndexOf(Base64Marker, StringComparison.Ordinal);
		if (marker < 0 || !IsImageMediaType(line[5..marker]))
		{
			return false;
		}

		var data = line.AsSpan(marker + Base64Marker.Length);
		return data.Length > 0 && !data.ContainsAnyExcept(Base64Chars);
	}

	private static bool IsImageMediaType(string? mediaType)
		=> ImageMediaTypes.Contains(mediaType, StringComparer.OrdinalIgnoreCase);
}
