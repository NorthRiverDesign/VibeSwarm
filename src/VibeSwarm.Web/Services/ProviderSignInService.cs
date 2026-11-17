using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.Utilities;

namespace VibeSwarm.Web.Services;

/// <summary>
/// Runs a provider CLI's own sign-in command on the host and relays it to the web UI, so an
/// expired login can be renewed from a phone. Claude Code (<c>claude auth login</c>) prints a
/// link and waits for the code its sign-in page shows; Copilot (<c>copilot login --device-code</c>)
/// prints GitHub's device link and a one-time code, then finishes by itself.
/// </summary>
public sealed class ProviderSignInService : IProviderSignInService, IDisposable
{
	private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(15);
	private const int MaxCodeLength = 2000;
	private const int MaxOutputLines = 40;

	private readonly Func<ProviderType, CancellationToken, Task<string?>> _resolveExecutable;
	private readonly Func<ProviderType, CancellationToken, Task>? _onSignedIn;
	private readonly ILogger<ProviderSignInService> _logger;
	private readonly TimeSpan _timeout;
	private readonly object _lock = new();
	private readonly SemaphoreSlim _startGate = new(1, 1);
	private readonly Dictionary<ProviderType, SignInRun> _runs = [];

	public ProviderSignInService(
		IServiceScopeFactory scopeFactory,
		ILogger<ProviderSignInService> logger,
		IProviderHealthTracker? healthTracker = null)
		: this(
			(providerType, cancellationToken) => ResolveExecutableAsync(scopeFactory, providerType, cancellationToken),
			(providerType, cancellationToken) => ResetProviderHealthAsync(scopeFactory, healthTracker, providerType, cancellationToken),
			logger,
			DefaultTimeout)
	{
	}

	internal ProviderSignInService(
		Func<ProviderType, CancellationToken, Task<string?>> resolveExecutable,
		Func<ProviderType, CancellationToken, Task>? onSignedIn,
		ILogger<ProviderSignInService> logger,
		TimeSpan timeout)
	{
		_resolveExecutable = resolveExecutable;
		_onSignedIn = onSignedIn;
		_logger = logger;
		_timeout = timeout;
	}

	public async Task<ProviderSignInSession> StartAsync(ProviderType providerType, CancellationToken cancellationToken = default)
	{
		await _startGate.WaitAsync(cancellationToken);
		try
		{
			lock (_lock)
			{
				if (_runs.TryGetValue(providerType, out var existing) && existing.Session.IsActive)
				{
					return Copy(existing.Session);
				}
			}

			var run = new SignInRun(new ProviderSignInSession
			{
				Id = Guid.NewGuid(),
				ProviderType = providerType,
				State = ProviderSignInState.Starting,
				StartedAt = DateTime.UtcNow
			});

			var startError = await TryStartProcessAsync(run, cancellationToken);
			lock (_lock)
			{
				if (startError != null)
				{
					run.Session.State = ProviderSignInState.Failed;
					run.Session.ErrorMessage = startError;
					run.Session.FinishedAt = DateTime.UtcNow;
				}

				_runs[providerType] = run;
			}

			if (startError == null)
			{
				_logger.LogInformation("Started {ProviderType} sign-in from the web UI", providerType);
				_ = Task.Run(() => MonitorAsync(run));
			}

			lock (_lock)
			{
				return Copy(run.Session);
			}
		}
		finally
		{
			_startGate.Release();
		}
	}

	public Task<ProviderSignInSession?> GetAsync(ProviderType providerType, CancellationToken cancellationToken = default)
	{
		lock (_lock)
		{
			return Task.FromResult(_runs.TryGetValue(providerType, out var run) ? Copy(run.Session) : null);
		}
	}

	public async Task<ProviderSignInSession> SubmitCodeAsync(ProviderType providerType, string code, CancellationToken cancellationToken = default)
	{
		// A pasted code can pick up line breaks; the CLI reads one line.
		var cleaned = (code ?? string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty).Trim();

		SignInRun? run;
		Process? process;
		lock (_lock)
		{
			process = _runs.TryGetValue(providerType, out run) ? run.Process : null;
			if (run == null || !run.Session.IsActive || process == null)
			{
				return new ProviderSignInSession
				{
					ProviderType = providerType,
					State = ProviderSignInState.Failed,
					ErrorMessage = "No sign-in is waiting for a code. Start signing in again.",
					StartedAt = DateTime.UtcNow,
					FinishedAt = DateTime.UtcNow
				};
			}

			if (!run.Session.NeedsCode)
			{
				run.Session.ErrorMessage = "This sign-in finishes on the sign-in page; there's no code to paste.";
				return Copy(run.Session);
			}

			if (cleaned.Length == 0 || cleaned.Length > MaxCodeLength)
			{
				run.Session.ErrorMessage = "Paste the code the sign-in page shows.";
				return Copy(run.Session);
			}

			run.Session.State = ProviderSignInState.Verifying;
			run.Session.ErrorMessage = null;
		}

		try
		{
			await process.StandardInput.WriteLineAsync(cleaned.AsMemory(), cancellationToken);
			await process.StandardInput.FlushAsync(cancellationToken);
		}
		catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
		{
			// The CLI already exited; the monitor records how it ended.
		}

		lock (_lock)
		{
			return Copy(run.Session);
		}
	}

	public Task<ProviderSignInSession?> CancelAsync(ProviderType providerType, CancellationToken cancellationToken = default)
	{
		SignInRun? run;
		var stop = false;
		lock (_lock)
		{
			if (!_runs.TryGetValue(providerType, out run))
			{
				return Task.FromResult<ProviderSignInSession?>(null);
			}

			if (run.Session.IsActive)
			{
				run.Session.State = ProviderSignInState.Cancelled;
				run.Session.FinishedAt = DateTime.UtcNow;
				stop = true;
			}
		}

		if (stop)
		{
			_logger.LogInformation("Cancelled {ProviderType} sign-in", providerType);
			Stop(run);
		}

		lock (_lock)
		{
			return Task.FromResult<ProviderSignInSession?>(Copy(run.Session));
		}
	}

	public void Dispose()
	{
		List<SignInRun> active;
		lock (_lock)
		{
			active = _runs.Values.Where(run => run.Session.IsActive).ToList();
			foreach (var run in active)
			{
				run.Session.State = ProviderSignInState.Cancelled;
				run.Session.FinishedAt = DateTime.UtcNow;
			}
		}

		// A sign-in left running would keep a child process in the service's cgroup.
		foreach (var run in active)
		{
			Stop(run);
			TryKill(run.Process);
		}
	}

	private async Task<string?> TryStartProcessAsync(SignInRun run, CancellationToken cancellationToken)
	{
		var providerType = run.Session.ProviderType;
		var displayName = GetDisplayName(providerType);
		if (!ProviderCapabilities.SupportsHostSignIn(providerType))
		{
			return $"{displayName} can't be signed in from here. Save an API key for it instead.";
		}

		string? executablePath;
		try
		{
			executablePath = await _resolveExecutable(providerType, cancellationToken);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			_logger.LogWarning(ex, "Could not find the {ProviderType} CLI to sign in", providerType);
			executablePath = null;
		}

		if (string.IsNullOrWhiteSpace(executablePath))
		{
			return $"{displayName} isn't installed on this host. Install it, then sign in.";
		}

		var startInfo = new ProcessStartInfo
		{
			FileName = executablePath,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8
		};
		foreach (var argument in GetSignInArguments(providerType))
		{
			startInfo.ArgumentList.Add(argument);
		}

		PlatformHelper.ConfigureForCrossPlatform(startInfo);

		var process = new Process { StartInfo = startInfo };
		try
		{
			process.Start();
		}
		catch (Exception ex)
		{
			process.Dispose();
			_logger.LogWarning(ex, "Could not start the {ProviderType} sign-in command", providerType);
			return $"Couldn't start {displayName}'s sign-in: {ex.Message}";
		}

		run.Process = process;
		run.Lifetime = new CancellationTokenSource(_timeout);
		return null;
	}

	/// <summary>
	/// The CLI's sign-in command. Copilot is held to the device code flow: with no flow flag it
	/// signs in from a GH_TOKEN in the service's environment instead of the account the user picks.
	/// </summary>
	internal static IReadOnlyList<string> GetSignInArguments(ProviderType providerType) => providerType switch
	{
		ProviderType.Claude => ["auth", "login"],
		ProviderType.Copilot => ["login", "--device-code"],
		_ => throw new NotSupportedException($"Provider type '{providerType}' can't be signed in from the web UI.")
	};

	private async Task MonitorAsync(SignInRun run)
	{
		var process = run.Process!;
		var lifetime = run.Lifetime!;
		var pumps = Task.WhenAll(
			PumpAsync(run, process.StandardOutput, isError: false),
			PumpAsync(run, process.StandardError, isError: true));

		int? exitCode = null;
		try
		{
			await process.WaitForExitAsync(lifetime.Token);
			exitCode = process.ExitCode;
		}
		catch (OperationCanceledException)
		{
			TryKill(process);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Lost track of the {ProviderType} sign-in command", run.Session.ProviderType);
			TryKill(process);
		}

		// Let the last lines in, so a failure can say what went wrong.
		await Task.WhenAny(pumps, Task.Delay(TimeSpan.FromSeconds(5)));

		var signedIn = Finish(run, exitCode);
		if (signedIn && _onSignedIn != null)
		{
			try
			{
				await _onSignedIn(run.Session.ProviderType, CancellationToken.None);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Could not clear {ProviderType} provider health after signing in", run.Session.ProviderType);
			}
		}

		lock (_lock)
		{
			run.Process = null;
			run.Lifetime = null;
		}

		lifetime.Dispose();
		process.Dispose();
	}

	private async Task PumpAsync(SignInRun run, StreamReader reader, bool isError)
	{
		try
		{
			while (await reader.ReadLineAsync() is { } line)
			{
				HandleLine(run, line, isError);
			}
		}
		catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
		{
			// The process was killed; nothing more to read.
		}
	}

	private void HandleLine(SignInRun run, string rawLine, bool isError)
	{
		var line = ProviderSignInOutput.Clean(rawLine);
		if (line.Length == 0)
		{
			return;
		}

		lock (_lock)
		{
			var lines = isError ? run.ErrorLines : run.OutputLines;
			lines.Add(line);
			if (lines.Count > MaxOutputLines)
			{
				lines.RemoveAt(0);
			}

			var session = run.Session;
			if (!session.IsActive)
			{
				return;
			}

			if (ProviderSignInOutput.TryParseDeviceCode(line, out var deviceUrl, out var userCode))
			{
				session.SignInUrl ??= deviceUrl;
				session.UserCode ??= userCode;
				session.NeedsCode = false;
				if (session.State == ProviderSignInState.Starting)
				{
					session.State = ProviderSignInState.WaitingForUser;
				}

				return;
			}

			if (!isError && session.SignInUrl == null && ProviderSignInOutput.TryParseUrl(line, out var url))
			{
				session.SignInUrl = url;
				// Claude Code's link ends on a page showing a code to paste back; Copilot's device
				// flow finishes on its own.
				session.NeedsCode = session.ProviderType == ProviderType.Claude;
				session.State = ProviderSignInState.WaitingForUser;
				return;
			}

			if (isError && ProviderSignInOutput.IsRejectedCode(line))
			{
				session.State = ProviderSignInState.WaitingForUser;
				session.ErrorMessage = "That isn't the whole code. Copy all of it from the sign-in page and paste it again.";
			}
		}
	}

	/// <summary>Records how the CLI ended. True when it signed in.</summary>
	private bool Finish(SignInRun run, int? exitCode)
	{
		ProviderSignInState state;
		lock (_lock)
		{
			var session = run.Session;
			if (!session.IsActive)
			{
				return false;
			}

			session.FinishedAt = DateTime.UtcNow;
			if (exitCode == 0)
			{
				session.State = ProviderSignInState.Succeeded;
				session.ErrorMessage = null;
				session.Message = ProviderSignInOutput.FindSuccessMessage(run.OutputLines) ?? "Signed in.";
			}
			else
			{
				session.State = ProviderSignInState.Failed;
				session.ErrorMessage = exitCode is null
					? $"Sign-in timed out after {_timeout.TotalMinutes:0} minutes. Start it again."
					: ProviderSignInOutput.DescribeFailure(run.ErrorLines, run.OutputLines, exitCode.Value);
			}

			state = session.State;
		}

		_logger.LogInformation("{ProviderType} sign-in ended: {State} (exit code {ExitCode})", run.Session.ProviderType, state, exitCode);
		return state == ProviderSignInState.Succeeded;
	}

	private static void Stop(SignInRun run)
	{
		try
		{
			run.Lifetime?.Cancel();
		}
		catch (ObjectDisposedException)
		{
			// Already finished.
		}
	}

	private static void TryKill(Process? process)
	{
		try
		{
			if (process is { HasExited: false })
			{
				process.Kill(entireProcessTree: true);
			}
		}
		catch
		{
			// Best effort: it may have exited on its own.
		}
	}

	private static ProviderSignInSession Copy(ProviderSignInSession session) => new()
	{
		Id = session.Id,
		ProviderType = session.ProviderType,
		State = session.State,
		SignInUrl = session.SignInUrl,
		UserCode = session.UserCode,
		NeedsCode = session.NeedsCode,
		Message = session.Message,
		ErrorMessage = session.ErrorMessage,
		StartedAt = session.StartedAt,
		FinishedAt = session.FinishedAt
	};

	private static string GetDisplayName(ProviderType providerType) => providerType switch
	{
		ProviderType.Claude => "Claude Code",
		ProviderType.Copilot => "GitHub Copilot",
		ProviderType.OpenCode => "OpenCode",
		_ => providerType.ToString()
	};

	private static async Task<string?> ResolveExecutableAsync(IServiceScopeFactory scopeFactory, ProviderType providerType, CancellationToken cancellationToken)
	{
		using var scope = scopeFactory.CreateScope();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
		var detectionService = scope.ServiceProvider.GetRequiredService<ProviderCliDetectionService>();

		var configuredPath = await dbContext.Providers
			.Where(provider => provider.Type == providerType
				&& provider.ConnectionMode == ProviderConnectionMode.CLI
				&& provider.ExecutablePath != null
				&& provider.ExecutablePath != string.Empty)
			.OrderByDescending(provider => provider.IsDefault)
			.ThenBy(provider => provider.Name)
			.Select(provider => provider.ExecutablePath)
			.FirstOrDefaultAsync(cancellationToken);

		var installSpec = CommonProviderSetupService.GetInstallSpec(providerType);
		var detection = await detectionService.DetectAsync(
			providerType,
			installSpec.ExecutableName,
			installSpec.VersionArguments,
			configuredPath,
			cancellationToken: cancellationToken);
		return detection.IsInstalled ? detection.ResolvedExecutablePath : null;
	}

	/// <summary>
	/// Failed runs from the expired login may have tripped the provider's circuit breaker; a fresh
	/// sign-in shouldn't leave jobs waiting out its cool-down.
	/// </summary>
	private static async Task ResetProviderHealthAsync(
		IServiceScopeFactory scopeFactory,
		IProviderHealthTracker? healthTracker,
		ProviderType providerType,
		CancellationToken cancellationToken)
	{
		if (healthTracker == null)
		{
			return;
		}

		using var scope = scopeFactory.CreateScope();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
		var providerIds = await dbContext.Providers
			.Where(provider => provider.Type == providerType)
			.Select(provider => provider.Id)
			.ToListAsync(cancellationToken);

		foreach (var providerId in providerIds)
		{
			healthTracker.ResetProvider(providerId);
		}
	}

	private sealed class SignInRun(ProviderSignInSession session)
	{
		public ProviderSignInSession Session { get; } = session;
		public Process? Process { get; set; }
		public CancellationTokenSource? Lifetime { get; set; }
		public List<string> OutputLines { get; } = [];
		public List<string> ErrorLines { get; } = [];
	}
}

/// <summary>Reads the sign-in commands' plain-text output.</summary>
internal static partial class ProviderSignInOutput
{
	/// <summary>Claude Code's paste prompt has no line break, so it prefixes whatever it prints next.</summary>
	[GeneratedRegex(@"^Paste code here[^>]*>\s*", RegexOptions.IgnoreCase)]
	private static partial Regex PastePrompt();

	[GeneratedRegex(@"\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)|\x1B\[[0-?]*[ -/]*[@-~]")]
	private static partial Regex AnsiEscape();

	/// <summary>"To authenticate, visit https://github.com/login/device and enter code ABCD-1234".</summary>
	[GeneratedRegex(@"visit\s+(?<url>https?://\S+?)\s+and\s+enter\s+(?:the\s+)?code\s+(?<code>[A-Za-z0-9]+(?:-[A-Za-z0-9]+)*)", RegexOptions.IgnoreCase)]
	private static partial Regex DeviceCode();

	[GeneratedRegex(@"https?://[^\s""'<>]+", RegexOptions.IgnoreCase)]
	private static partial Regex Url();

	/// <summary>Lines about the host failing to open a browser or clipboard, which don't matter here.</summary>
	private static readonly string[] NoiseMarkers =
	[
		"failed to copy",
		"failed to open browser",
		"open the link above"
	];

	public static string Clean(string line) =>
		PastePrompt().Replace(AnsiEscape().Replace(line, string.Empty).Trim(), string.Empty).Trim();

	public static bool TryParseDeviceCode(string line, out string url, out string userCode)
	{
		var match = DeviceCode().Match(line);
		url = match.Success ? match.Groups["url"].Value : string.Empty;
		userCode = match.Success ? match.Groups["code"].Value : string.Empty;
		return match.Success;
	}

	public static bool TryParseUrl(string line, out string url)
	{
		var match = Url().Match(line);
		url = match.Success ? match.Value.TrimEnd('.', ',', ';', ')') : string.Empty;
		return match.Success;
	}

	/// <summary>Claude Code's reply to a paste that isn't a whole <c>code#state</c>; it keeps waiting.</summary>
	public static bool IsRejectedCode(string line) =>
		line.StartsWith("Invalid code", StringComparison.OrdinalIgnoreCase);

	public static string? FindSuccessMessage(IReadOnlyList<string> outputLines) =>
		outputLines.LastOrDefault(line =>
			line.Contains("signed in", StringComparison.OrdinalIgnoreCase) ||
			line.Contains("login successful", StringComparison.OrdinalIgnoreCase));

	public static string DescribeFailure(IReadOnlyList<string> errorLines, IReadOnlyList<string> outputLines, int exitCode)
	{
		var allLines = errorLines.Concat(outputLines).ToList();

		// Copilot signs in but can't keep the token without a keychain unless a person at a
		// terminal agrees to plain-text storage.
		if (allLines.Any(line => line.Contains("token was not saved", StringComparison.OrdinalIgnoreCase)))
		{
			return "Signed in, but this host has no keychain to keep the token. Save a GitHub token with \"Use an API key\" instead.";
		}

		var reported = errorLines.LastOrDefault(line => line.StartsWith("Login failed:", StringComparison.OrdinalIgnoreCase));
		if (reported != null)
		{
			var reason = reported["Login failed:".Length..].Trim();
			return reason.Length > 0 ? reason : reported;
		}

		var lastError = errorLines.LastOrDefault(line => !IsNoise(line) && !IsRejectedCode(line));
		if (lastError != null)
		{
			return lastError;
		}

		return outputLines.LastOrDefault(line => !IsNoise(line)
				&& !Url().IsMatch(line)
				&& !line.Contains("waiting for authorization", StringComparison.OrdinalIgnoreCase)
				&& !line.StartsWith("Opening browser", StringComparison.OrdinalIgnoreCase))
			?? $"The sign-in command stopped with exit code {exitCode}.";
	}

	private static bool IsNoise(string line) =>
		NoiseMarkers.Any(marker => line.Contains(marker, StringComparison.OrdinalIgnoreCase));
}
