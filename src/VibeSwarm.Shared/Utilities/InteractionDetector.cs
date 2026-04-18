using System.Text.RegularExpressions;

namespace VibeSwarm.Shared.Utilities;

public static class InteractionDetector
{
	public class InteractionRequest
	{
		public bool IsInteractionRequested { get; set; }
		public InteractionType Type { get; set; } = InteractionType.Unknown;
		public string? Prompt { get; set; }
		public List<string>? Choices { get; set; }
		public string? DefaultResponse { get; set; }
		// Confidence score (0-1) of the detection
		public double Confidence { get; set; }
		public string? RawOutput { get; set; }
	}

	public enum InteractionType
	{
		Unknown,
		Confirmation,
		TextInput,
		Choice,
		Permission,
		Continue,
		Authentication
	}

	// Common patterns that indicate interaction is being requested
	private static readonly (Regex Pattern, InteractionType Type, double Confidence, string? DefaultHint)[] InteractionPatterns = new[]
	{
        // Yes/No confirmations
        (new Regex(@"\?\s*\[?[Yy](?:es)?[/,][Nn](?:o)?\]?\s*[:>]?\s*$", RegexOptions.Compiled),
			InteractionType.Confirmation, 0.95, "y"),
		(new Regex(@"\(y(?:es)?/n(?:o)?\)\s*[:>]?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase),
			InteractionType.Confirmation, 0.95, "y"),
		(new Regex(@"(?:confirm|proceed|continue)\?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase),
			InteractionType.Confirmation, 0.85, "y"),
		(new Regex(@"(?:are you sure|do you want to)\s*.+\?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase),
			InteractionType.Confirmation, 0.80, null),
        
        // Permission requests (common in AI agents)
        (new Regex(@"(?:allow|permit|grant|approve)\s+(?:this|the)?\s*(?:action|operation|tool|command)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
			InteractionType.Permission, 0.90, null),
		(new Regex(@"(?:waiting for|requires?)\s+(?:your\s+)?(?:approval|permission|confirmation)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
			InteractionType.Permission, 0.90, null),
		(new Regex(@"(?:press enter to continue|hit enter|type enter)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
			InteractionType.Continue, 0.85, ""),
        
        // Claude Code specific patterns
        (new Regex(@"Do you want to allow", RegexOptions.Compiled | RegexOptions.IgnoreCase),
			InteractionType.Permission, 0.95, "y"),
		(new Regex(@"Would you like (?:me )?to", RegexOptions.Compiled | RegexOptions.IgnoreCase),
			InteractionType.Confirmation, 0.75, "y"),
		(new Regex(@"Shall I (?:proceed|continue)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
			InteractionType.Confirmation, 0.80, "y"),
        
        // GitHub Copilot CLI patterns
        (new Regex(@"Accept\s+this\s+suggestion", RegexOptions.Compiled | RegexOptions.IgnoreCase),
			InteractionType.Permission, 0.90, "y"),
		(new Regex(@"\[Accept\]|\[Reject\]|\[Edit\]", RegexOptions.Compiled),
			InteractionType.Choice, 0.90, null),
        
        // Generic input prompts
        (new Regex(@"(?:enter|input|type|provide)\s+(?:a|your|the)\s+\w+\s*[:>]\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase),
			InteractionType.TextInput, 0.75, null),
		(new Regex(@":\s*$", RegexOptions.Compiled),
			InteractionType.TextInput, 0.30, null), // Low confidence - colons can appear normally
        (new Regex(@">\s*$", RegexOptions.Compiled),
			InteractionType.TextInput, 0.25, null), // Low confidence - prompts often end with >
        
        // Choice/selection prompts
        (new Regex(@"(?:select|choose|pick)\s+(?:an?\s+)?(?:option|choice|number)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
			InteractionType.Choice, 0.85, null),
		(new Regex(@"\[\d+\]\s+\w+", RegexOptions.Compiled), // Numbered options like [1] Option
            InteractionType.Choice, 0.70, null),
        
        // Authentication prompts
        (new Regex(@"(?:password|token|api.?key|secret)\s*[:>]\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase),
			InteractionType.Authentication, 0.90, null),
		(new Regex(@"(?:login|sign.?in|authenticate)\s*[:>]?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase),
			InteractionType.Authentication, 0.85, null),
        
        // Waiting/stalled indicators (suggests interaction needed)
        (new Regex(@"waiting\s+for\s+(?:user\s+)?(?:input|response)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
			InteractionType.TextInput, 0.90, null),
	};

	// Patterns that indicate the process is NOT waiting for input (false positives to filter)
	private static readonly Regex[] NonInteractionPatterns = new[]
	{
		new Regex(@"^\s*\{", RegexOptions.Compiled), // JSON output
        new Regex(@"^\s*\[", RegexOptions.Compiled), // JSON array
        new Regex(@"^[A-Z_]+\s*=", RegexOptions.Compiled), // Environment variable
        new Regex(@"^\d{4}-\d{2}-\d{2}", RegexOptions.Compiled), // Timestamp
        new Regex(@"^(?:DEBUG|INFO|WARN|ERROR|TRACE)[\s:]", RegexOptions.Compiled | RegexOptions.IgnoreCase), // Log level
        new Regex(@"^\s*#", RegexOptions.Compiled), // Comment
        new Regex(@"Running\s+tool", RegexOptions.Compiled | RegexOptions.IgnoreCase), // Tool execution
        new Regex(@"(?:Reading|Writing|Creating|Updating|Deleting)\s+file", RegexOptions.Compiled | RegexOptions.IgnoreCase), // File operations
    };

	public static InteractionRequest? DetectInteraction(string outputLine, IEnumerable<string>? recentContext = null)
	{
		if (string.IsNullOrWhiteSpace(outputLine))
			return null;

		var trimmedLine = outputLine.Trim();

		// Filter out obvious non-interaction patterns
		foreach (var pattern in NonInteractionPatterns)
		{
			if (pattern.IsMatch(trimmedLine))
				return null;
		}

		// Check each interaction pattern
		InteractionRequest? bestMatch = null;

		foreach (var (pattern, type, confidence, defaultHint) in InteractionPatterns)
		{
			if (pattern.IsMatch(trimmedLine))
			{
				var request = new InteractionRequest
				{
					IsInteractionRequested = true,
					Type = type,
					Prompt = ExtractPrompt(trimmedLine, type),
					Confidence = confidence,
					DefaultResponse = defaultHint,
					RawOutput = outputLine
				};

				// Extract choices if applicable
				if (type == InteractionType.Choice || type == InteractionType.Confirmation)
				{
					request.Choices = ExtractChoices(trimmedLine, recentContext);
				}

				// Keep the highest confidence match
				if (bestMatch == null || request.Confidence > bestMatch.Confidence)
				{
					bestMatch = request;
				}
			}
		}

		// Only return if confidence is above threshold
		if (bestMatch != null && bestMatch.Confidence >= 0.50)
		{
			return bestMatch;
		}

		return null;
	}

	private static string ExtractPrompt(string line, InteractionType type)
	{
		var prompt = line.Trim();

		// Remove common suffixes
		prompt = Regex.Replace(prompt, @"\s*\[[YyNn](?:es)?(?:/[YyNn](?:o)?)?\]\s*[:>]?\s*$", "");
		prompt = Regex.Replace(prompt, @"\s*\([YyNn](?:es)?/[YyNn](?:o)?\)\s*[:>]?\s*$", "");
		prompt = Regex.Replace(prompt, @"\s*[:>]\s*$", "");

		return prompt.Trim();
	}

	private static List<string>? ExtractChoices(string line, IEnumerable<string>? context)
	{
		var choices = new List<string>();

		// Check for y/n style choices
		if (Regex.IsMatch(line, @"[Yy](?:es)?[/,][Nn](?:o)?", RegexOptions.IgnoreCase))
		{
			choices.Add("y");
			choices.Add("n");
			return choices;
		}

		// Check for numbered options in context
		if (context != null)
		{
			foreach (var contextLine in context)
			{
				var match = Regex.Match(contextLine.Trim(), @"^\[?(\d+)\]?\s*[.):]\s*(.+)$");
				if (match.Success)
				{
					choices.Add(match.Groups[2].Value.Trim());
				}
			}
		}

		// Check for bracketed options
		var bracketMatches = Regex.Matches(line, @"\[([^\]]+)\]");
		foreach (Match match in bracketMatches)
		{
			var option = match.Groups[1].Value;
			if (!string.IsNullOrWhiteSpace(option) && option.Length < 20)
			{
				choices.Add(option);
			}
		}

		return choices.Count > 0 ? choices : null;
	}

	private static bool HasSupportingContext(IEnumerable<string> context, InteractionType type)
	{
		var contextText = string.Join(" ", context);

		return type switch
		{
			InteractionType.Confirmation =>
				Regex.IsMatch(contextText, @"(?:confirm|approve|accept|proceed|continue)", RegexOptions.IgnoreCase),
			InteractionType.Permission =>
				Regex.IsMatch(contextText, @"(?:permission|allow|access|grant|authorize)", RegexOptions.IgnoreCase),
			InteractionType.Choice =>
				Regex.IsMatch(contextText, @"(?:select|choose|option|which)", RegexOptions.IgnoreCase),
			InteractionType.Authentication =>
				Regex.IsMatch(contextText, @"(?:login|auth|credential|password|token)", RegexOptions.IgnoreCase),
			_ => false
		};
	}
}
