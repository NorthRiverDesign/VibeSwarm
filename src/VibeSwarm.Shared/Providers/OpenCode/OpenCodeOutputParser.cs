using System.Text.Json;

namespace VibeSwarm.Shared.Providers.OpenCode;

/// <summary>
/// Helper methods for OpenCode-specific output parsing and model validation.
/// </summary>
public static class OpenCodeOutputParser
{
	/// <summary>
	/// Parses the output of `opencode models` command to extract model names.
	/// Filters out log/info lines and returns only valid model identifiers.
	/// </summary>
	public static List<string> ParseModelsOutput(string output)
	{
		var models = new List<string>();
		var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

		foreach (var line in lines)
		{
			var trimmedLine = line.Trim();

			if (string.IsNullOrWhiteSpace(trimmedLine))
				continue;

			if (IsLogLine(trimmedLine))
				continue;

			if (IsValidModelName(trimmedLine))
			{
				models.Add(trimmedLine);
			}
		}

		return models;
	}

	/// <summary>
	/// Determines if a line appears to be a log/info line that should be filtered out.
	/// </summary>
	public static bool IsLogLine(string line)
	{
		var logPrefixes = new[] { "INFO", "WARN", "WARNING", "ERROR", "DEBUG", "TRACE", "FATAL" };
		foreach (var prefix in logPrefixes)
		{
			if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
				(line.Length == prefix.Length || char.IsWhiteSpace(line[prefix.Length]) || line[prefix.Length] == ':'))
			{
				return true;
			}
		}

		// Check for timestamp patterns at the start
		if (line.Length > 10 && (char.IsDigit(line[0]) || line[0] == '[') && line.Contains('-') && line.Contains(':'))
		{
			if (System.Text.RegularExpressions.Regex.IsMatch(line, @"^\[?\d{4}-\d{2}-\d{2}"))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Validates if a string looks like a valid model name.
	/// </summary>
	public static bool IsValidModelName(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
			return false;

		// Model names should not contain spaces
		if (name.Contains(' '))
			return false;

		// Model names typically follow patterns like "provider/model" or "model:tag"
		foreach (var c in name)
		{
			if (!char.IsLetterOrDigit(c) && c != '/' && c != ':' && c != '-' && c != '_' && c != '.')
			{
				return false;
			}
		}

		return true;
	}

	/// <summary>
	/// Gets a default pricing multiplier based on the model name.
	/// </summary>
	public static decimal GetDefaultModelMultiplier(string modelName)
	{
		var lowerName = modelName.ToLowerInvariant();

		// High-tier models (opus, large, max, etc.)
		if (lowerName.Contains("opus") || lowerName.Contains("large") || lowerName.Contains("max") || lowerName.Contains("big"))
			return 5.0m;

		// Mid-tier models (sonnet, medium, pro)
		if (lowerName.Contains("sonnet") || lowerName.Contains("medium") || lowerName.Contains("pro"))
			return 1.0m;

		// Low-tier models (haiku, mini, small, nano)
		if (lowerName.Contains("haiku") || lowerName.Contains("mini") || lowerName.Contains("small") || lowerName.Contains("nano"))
			return 0.25m;

		// Local/Ollama models are typically free or very low cost
		if (lowerName.StartsWith("ollama/") || lowerName.StartsWith("local/"))
			return 0.01m;

		// Default multiplier for unknown models
		return 1.0m;
	}

	/// <summary>
	/// Attempts to extract meaningful error information from CLI output.
	/// </summary>
	public static string? ExtractErrorFromOutput(List<string> outputLines)
	{
		var errorLines = new List<string>();

		foreach (var line in outputLines)
		{
			if (string.IsNullOrWhiteSpace(line)) continue;

			// Try to parse as JSON and look for error fields
			try
			{
				using var doc = JsonDocument.Parse(line);
				var root = doc.RootElement;

				// Check for error type events
				if (root.TryGetProperty("type", out var typeProp))
				{
					var eventType = typeProp.GetString()?.ToLowerInvariant();
					if (eventType == "error" || eventType == "fatal" || eventType == "panic")
					{
						var errorText = GetJsonStringProperty(root, "error")
							?? GetJsonStringProperty(root, "message")
							?? GetJsonStringProperty(root, "content")
							?? GetJsonStringProperty(root, "detail")
							?? GetJsonStringProperty(root, "reason");

						if (!string.IsNullOrWhiteSpace(errorText) && !errorLines.Contains(errorText))
						{
							errorLines.Add(errorText);
						}
					}
				}

				// Check for error field on any event type
				var anyError = GetJsonStringProperty(root, "error");
				if (!string.IsNullOrWhiteSpace(anyError) && !errorLines.Contains(anyError))
				{
					errorLines.Add(anyError);
				}

				// Check for common error indicator fields
				var errorMsg = GetJsonStringProperty(root, "error_message")
					?? GetJsonStringProperty(root, "errorMessage")
					?? GetJsonStringProperty(root, "err");
				if (!string.IsNullOrWhiteSpace(errorMsg) && !errorLines.Contains(errorMsg))
				{
					errorLines.Add(errorMsg);
				}
			}
			catch
			{
				// Not JSON - check for common error patterns in plain text
				var trimmedLine = line.Trim();
				var lowerLine = trimmedLine.ToLowerInvariant();

				if (lowerLine.StartsWith("error:") ||
					lowerLine.StartsWith("error ") ||
					lowerLine.StartsWith("failed:") ||
					lowerLine.StartsWith("fatal:") ||
					lowerLine.StartsWith("panic:") ||
					lowerLine.StartsWith("exception:") ||
					lowerLine.Contains("error:") ||
					lowerLine.Contains("failed to") ||
					lowerLine.Contains("cannot ") ||
					lowerLine.Contains("unable to") ||
					lowerLine.Contains("not found") ||
					lowerLine.Contains("invalid ") ||
					lowerLine.Contains("no such") ||
					(lowerLine.Contains("error") && (lowerLine.Contains("model") || lowerLine.Contains("api") || lowerLine.Contains("key"))))
				{
					if (!errorLines.Contains(trimmedLine))
					{
						errorLines.Add(trimmedLine);
					}
				}
			}
		}

		return errorLines.Count > 0 ? string.Join("\n", errorLines.Take(15)) : null;
	}

	/// <summary>
	/// Safely gets a string property from a JSON element.
	/// </summary>
	public static string? GetJsonStringProperty(JsonElement element, string propertyName)
	{
		if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
		{
			return prop.GetString();
		}
		return null;
	}

	/// <summary>
	/// Parses OpenCode session show output to extract summary information.
	/// </summary>
	public static string? ParseSessionOutput(string output)
	{
		if (string.IsNullOrWhiteSpace(output))
			return null;

		try
		{
			using var doc = JsonDocument.Parse(output);
			var root = doc.RootElement;

			if (root.TryGetProperty("summary", out var summaryProp))
			{
				return summaryProp.GetString();
			}

			if (root.TryGetProperty("description", out var descProp))
			{
				return descProp.GetString();
			}

			if (root.TryGetProperty("messages", out var messagesProp))
			{
				var lastAssistantMessage = string.Empty;
				foreach (var msg in messagesProp.EnumerateArray())
				{
					if (msg.TryGetProperty("role", out var roleProp) &&
						roleProp.GetString() == "assistant" &&
						msg.TryGetProperty("content", out var contentProp))
					{
						lastAssistantMessage = contentProp.GetString() ?? string.Empty;
					}
				}

				if (!string.IsNullOrWhiteSpace(lastAssistantMessage) && lastAssistantMessage.Length <= 500)
				{
					return lastAssistantMessage;
				}
			}
		}
		catch
		{
			// Not valid JSON
		}

		return null;
	}

	/// <summary>
	/// Cleans OpenCode CLI output to extract the actual response.
	/// </summary>
	public static string CleanOutput(string output, int maxLength = 500)
	{
		if (string.IsNullOrWhiteSpace(output))
			return string.Empty;

		var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
		var textContent = new System.Text.StringBuilder();

		foreach (var line in lines)
		{
			if (string.IsNullOrWhiteSpace(line)) continue;

			try
			{
				using var doc = JsonDocument.Parse(line);
				var root = doc.RootElement;

				if (root.TryGetProperty("type", out var typeProp))
				{
					var type = typeProp.GetString();
					if (type == "message" || type == "assistant")
					{
						if (root.TryGetProperty("content", out var contentProp))
						{
							textContent.Append(contentProp.GetString());
						}
					}
					else if (type == "done" || type == "complete")
					{
						if (root.TryGetProperty("output", out var outputProp))
						{
							var resultText = outputProp.GetString();
							if (!string.IsNullOrWhiteSpace(resultText))
							{
								return resultText.Trim();
							}
						}
					}
				}
			}
			catch
			{
				// Not JSON, accumulate as plain text
				if (!line.StartsWith("{"))
				{
					textContent.AppendLine(line);
				}
			}
		}

		var result = textContent.ToString().Trim();
		return result.Length <= maxLength ? result : result[..maxLength];
	}
}
