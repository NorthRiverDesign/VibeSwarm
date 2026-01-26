using System.Text.RegularExpressions;

namespace VibeSwarm.Shared.Utilities;

/// <summary>
/// Detects when a CLI agent is requesting user interaction/input.
/// Analyzes output patterns to identify prompts, confirmations, and questions.
/// </summary>
public static class InteractionDetector
{
	/// <summary>
	/// Result of interaction detection analysis
	/// </summary>
	public class InteractionRequest
	{
		/// <summary>
		/// Whether an interaction is being requested
		/// </summary>
		public bool IsInteractionRequested { get; set; }

		/// <summary>
		/// The type of interaction being requested
		/// </summary>
		public InteractionType Type { get; set; } = InteractionType.Unknown;

		/// <summary>
		/// The prompt or question being asked
		/// </summary>
		public string? Prompt { get; set; }

		/// <summary>
		/// Available choices if this is a choice-type interaction (e.g., y/n, options list)
		/// </summary>
		public List<string>? Choices { get; set; }

		/// <summary>
		/// Suggested default response if available
		/// </summary>
		public string? DefaultResponse { get; set; }

		/// <summary>
		/// Confidence score (0-1) of the detection
		/// </summary>
		public double Confidence { get; set; }

		/// <summary>
		/// The raw output line that triggered the detection
		/// </summary>
		public string? RawOutput { get; set; }
	}

	/// <summary>
	/// Types of interactions that can be detected
	/// </summary>
	public enum InteractionType
	{
		Unknown,
		/// <summary>
		/// Yes/No confirmation (y/n, yes/no)
		/// </summary>
		Confirmation,
		/// <summary>
		/// Free-form text input
		/// </summary>
		TextInput,
		/// <summary>
		/// Multiple choice selection
		/// </summary>
		Choice,
		/// <summary>
		/// Permission request (allow/deny)
		/// </summary>
		Permission,
		/// <summary>
		/// Continue/abort prompt
		/// </summary>
		Continue,
		/// <summary>
		/// Authentication or credential prompt
		/// </summary>
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

	/// <summary>
	/// Analyzes output text to detect if user interaction is being requested.
	/// </summary>
	/// <param name="outputLine">A single line of output to analyze</param>
	/// <param name="recentContext">Recent output lines for context (optional)</param>
	/// <returns>Interaction request details if detected</returns>
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

	/// <summary>
	/// Analyzes multiple lines of output to detect interaction requests.
	/// Uses context to improve detection accuracy.
	/// </summary>
	/// <param name="outputLines">Recent output lines to analyze</param>
	/// <param name="maxLinesToCheck">Maximum number of recent lines to check</param>
	/// <returns>Interaction request details if detected</returns>
	public static InteractionRequest? DetectInteractionFromOutput(IEnumerable<string> outputLines, int maxLinesToCheck = 10)
	{
		var lines = outputLines.TakeLast(maxLinesToCheck).ToList();

		// Check from most recent to oldest
		for (int i = lines.Count - 1; i >= 0; i--)
		{
			var context = i > 0 ? lines.Take(i) : null;
			var result = DetectInteraction(lines[i], context);

			if (result != null && result.IsInteractionRequested)
			{
				// Boost confidence if we see interaction patterns in context
				if (context != null && HasSupportingContext(context, result.Type))
				{
					result.Confidence = Math.Min(1.0, result.Confidence + 0.1);
				}

				return result;
			}
		}

		return null;
	}

	/// <summary>
	/// Checks if the process output suggests it's stalled waiting for input.
	/// This is based on output timing patterns.
	/// </summary>
	/// <param name="lastOutputTime">When the last output was received</param>
	/// <param name="lastOutputLine">The last output line</param>
	/// <param name="stallThreshold">How long without output before considered stalled</param>
	/// <returns>True if the process appears to be waiting for input</returns>
	public static bool IsLikelyWaitingForInput(
		DateTime lastOutputTime,
		string? lastOutputLine,
		TimeSpan stallThreshold)
	{
		if (string.IsNullOrEmpty(lastOutputLine))
			return false;

		var timeSinceLastOutput = DateTime.UtcNow - lastOutputTime;

		// If we've been stalled for a while, check if the last line looks like a prompt
		if (timeSinceLastOutput > stallThreshold)
		{
			var interaction = DetectInteraction(lastOutputLine);
			// Even low confidence detections become more significant when stalled
			if (interaction != null && interaction.Confidence >= 0.25)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Extracts the main prompt text from an interaction line
	/// </summary>
	private static string ExtractPrompt(string line, InteractionType type)
	{
		var prompt = line.Trim();

		// Remove common suffixes
		prompt = Regex.Replace(prompt, @"\s*\[[YyNn](?:es)?(?:/[YyNn](?:o)?)?\]\s*[:>]?\s*$", "");
		prompt = Regex.Replace(prompt, @"\s*\([YyNn](?:es)?/[YyNn](?:o)?\)\s*[:>]?\s*$", "");
		prompt = Regex.Replace(prompt, @"\s*[:>]\s*$", "");

		return prompt.Trim();
	}

	/// <summary>
	/// Extracts available choices from the output
	/// </summary>
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

	/// <summary>
	/// Checks if context lines support the detected interaction type
	/// </summary>
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

	/// <summary>
	/// Suggests an automatic response based on the interaction type.
	/// Used when running in non-interactive mode.
	/// </summary>
	/// <param name="request">The detected interaction request</param>
	/// <param name="autoApprovePermissions">Whether to auto-approve permission requests</param>
	/// <returns>Suggested response or null if manual input is required</returns>
	public static string? SuggestAutoResponse(InteractionRequest request, bool autoApprovePermissions = true)
	{
		if (request == null || !request.IsInteractionRequested)
			return null;

		return request.Type switch
		{
			InteractionType.Confirmation when request.DefaultResponse != null => request.DefaultResponse,
			InteractionType.Permission when autoApprovePermissions => "y",
			InteractionType.Continue => "", // Empty string for Enter key
			_ => null // Requires manual input
		};
	}
}
