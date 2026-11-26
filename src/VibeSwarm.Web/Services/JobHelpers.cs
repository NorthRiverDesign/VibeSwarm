using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace VibeSwarm.Web.Services;

/// <summary>
/// One helper a running job can ask: the project's Decisions or Research profile, resolved when
/// the run starts.
/// </summary>
public sealed record JobHelper(
	string Name,
	string Purpose,
	string ProfileName,
	string ProviderName,
	string? ModelId,
	ConsultTarget Target,
	bool ReadsRepository);

/// <summary>
/// What a run's <c>vibeswarm-ask</c> command may do: ask its helpers with its token, one
/// question at a time and a few per run, until the run ends.
/// </summary>
public sealed class JobHelperSession : IDisposable
{
	private int _questionsAsked;

	public required Guid JobId { get; init; }
	public required string Token { get; init; }
	public required IReadOnlyList<JobHelper> Helpers { get; init; }

	/// <summary>Where a helper that reads the code looks: the run's checkout or worktree.</summary>
	public string? WorkingDirectory { get; init; }

	/// <summary>Held while a question is out, so a run asks one at a time.</summary>
	public SemaphoreSlim Gate { get; } = new(1, 1);

	/// <summary>Cancelled when the run ends, stopping any question still being answered.</summary>
	public CancellationTokenSource Ended { get; } = new();

	/// <summary>Counts a question against the run's allowance; false once it is spent.</summary>
	public bool TryCountQuestion(int limit) => Interlocked.Increment(ref _questionsAsked) <= limit;

	public JobHelper? Find(string? name)
		=> string.IsNullOrWhiteSpace(name)
			? null
			: Helpers.FirstOrDefault(helper => string.Equals(helper.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

	public void Dispose()
	{
		try
		{
			Ended.Cancel();
		}
		catch (ObjectDisposedException)
		{
		}

		Ended.Dispose();
	}
}

/// <summary>The helper sessions of the runs in progress, by job.</summary>
public sealed class JobHelperSessions
{
	private readonly ConcurrentDictionary<Guid, JobHelperSession> _sessions = new();

	public void Register(JobHelperSession session)
	{
		if (_sessions.TryGetValue(session.JobId, out var previous))
		{
			previous.Dispose();
		}

		_sessions[session.JobId] = session;
	}

	/// <summary>The job's session when <paramref name="token"/> is its token; null otherwise.</summary>
	public JobHelperSession? Find(Guid jobId, string? token)
	{
		if (string.IsNullOrEmpty(token) || !_sessions.TryGetValue(jobId, out var session))
		{
			return null;
		}

		var expected = Encoding.UTF8.GetBytes(session.Token);
		var actual = Encoding.UTF8.GetBytes(token);
		return CryptographicOperations.FixedTimeEquals(expected, actual) ? session : null;
	}

	public void Remove(Guid jobId)
	{
		if (_sessions.TryRemove(jobId, out var session))
		{
			session.Dispose();
		}
	}
}

/// <summary>Writes the command a run uses to ask its helpers, and finds where it can reach VibeSwarm.</summary>
public static class JobHelperCommand
{
	public const string TokenHeader = "X-VibeSwarm-Token";
	public const string HelperHeader = "X-VibeSwarm-Helper";

	/// <summary>
	/// How long a helper gets to answer. Below Claude Code's ten-minute limit on a shell
	/// command, so the agent hears the answer, or why there is none, before its tool gives up.
	/// </summary>
	public static readonly TimeSpan AnswerTimeout = TimeSpan.FromMinutes(8);

	/// <summary>
	/// The command's path for a job: outside the checkout and its worktrees, and the same on every
	/// run, so a resumed session's remembered command still works.
	/// </summary>
	public static string GetScriptPath(Guid jobId)
		=> Path.Combine(Path.GetTempPath(), "vibeswarm", "helpers", jobId.ToString("N"), "vibeswarm-ask");

	public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

	/// <summary>
	/// A loopback address VibeSwarm listens on, preferring plain HTTP; null when it can't tell.
	/// </summary>
	public static string? FindLoopbackBaseUrl(IServer? server)
	{
		var addresses = server?.Features.Get<IServerAddressesFeature>()?.Addresses;
		if (addresses == null || addresses.Count == 0)
		{
			return null;
		}

		return addresses
			.Select(ToLoopback)
			.Where(url => url != null)
			.OrderBy(url => url!.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
			.FirstOrDefault();
	}

	internal static string? ToLoopback(string address)
	{
		var separator = address.IndexOf("://", StringComparison.Ordinal);
		if (separator < 0)
		{
			return null;
		}

		var scheme = address[..separator].ToLowerInvariant();
		var rest = address[(separator + 3)..].TrimEnd('/');
		var portSeparator = rest.LastIndexOf(':');
		if (scheme is not ("http" or "https") || portSeparator < 0 || !int.TryParse(rest[(portSeparator + 1)..], out var port))
		{
			return null;
		}

		var host = rest[..portSeparator];
		host = host is "0.0.0.0" or "+" or "*" or "[::]" or "localhost" or "" ? "127.0.0.1" : host;
		return $"{scheme}://{host}:{port}";
	}

	/// <summary>Whether curl, which the command runs, is on the PATH.</summary>
	public static bool IsCurlAvailable()
		=> (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
			.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
			.Any(directory => File.Exists(Path.Combine(directory, "curl")));

	/// <summary>Writes the command, readable only by VibeSwarm's user, and returns its path.</summary>
	public static string WriteScript(Guid jobId, string baseUrl, string token)
	{
		var path = GetScriptPath(jobId);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, BuildScript($"{baseUrl}/api/jobs/{jobId}/helpers/ask", token, baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)));
		if (!OperatingSystem.IsWindows())
		{
			File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		}

		return path;
	}

	public static void DeleteScript(Guid jobId)
	{
		try
		{
			var directory = Path.GetDirectoryName(GetScriptPath(jobId))!;
			if (Directory.Exists(directory))
			{
				Directory.Delete(directory, recursive: true);
			}
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}

	internal static string BuildScript(string url, string token, bool selfSigned)
	{
		var insecure = selfSigned ? " -k" : string.Empty;
		var maxTime = (int)AnswerTimeout.TotalSeconds + 30;
		var curl = $"curl -sS{insecure} --max-time {maxTime} -H '{TokenHeader}: {token}' -H \"{HelperHeader}: $helper\" -H 'Content-Type: text/plain; charset=utf-8' --data-binary @- '{url}'";
		return $"""
			#!/bin/sh
			# Asks one of this VibeSwarm job's helpers a question and prints the answer.
			# Usage: vibeswarm-ask <helper> [question]   (or pipe the question in)
			if [ -z "$1" ]; then
				echo "Usage: vibeswarm-ask <helper> [question], or pipe the question in." >&2
				exit 2
			fi
			helper="$1"
			shift
			if [ "$#" -gt 0 ]; then
				printf '%s' "$*" | {curl}
			else
				{curl}
			fi
			echo

			""";
	}
}
