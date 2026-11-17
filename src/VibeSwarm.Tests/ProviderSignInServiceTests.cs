using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

/// <summary>
/// Drives the sign-in service with small shell scripts that print what the real CLIs print
/// (captured from <c>claude auth login</c> 2.1.322 and <c>copilot login --device-code</c> 1.0.109).
/// </summary>
public sealed class ProviderSignInServiceTests : IDisposable
{
	private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(20);
	private readonly string _directory;

	public ProviderSignInServiceTests()
	{
		_directory = Path.Combine(Path.GetTempPath(), "vibeswarm-sign-in-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_directory);
	}

	[Fact]
	public async Task ClaudeSignIn_ShowsTheLink_RejectsAPartialCode_AndFinishesWithTheWholeOne()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		var script = WriteScript("""
			echo "Opening browser to sign in…"
			echo "If the browser didn't open, visit: https://claude.com/cai/oauth/authorize?code=true&client_id=abc&state=xyz"
			printf "Paste code here if prompted > "
			while IFS= read -r line; do
				case "$line" in
					*#*) echo "Login successful."; exit 0 ;;
					*) echo "Invalid code. Please make sure the full code was copied." >&2 ;;
				esac
			done
			exit 1
			""");
		var signedIn = new List<ProviderType>();
		using var service = CreateService(script, signedIn);

		var started = await service.StartAsync(ProviderType.Claude);
		var waiting = await WaitForAsync(service, ProviderType.Claude, session => session.State == ProviderSignInState.WaitingForUser);

		Assert.Equal(started.Id, waiting.Id);
		Assert.True(waiting.NeedsCode);
		Assert.Null(waiting.UserCode);
		Assert.Equal("https://claude.com/cai/oauth/authorize?code=true&client_id=abc&state=xyz", waiting.SignInUrl);

		var verifying = await service.SubmitCodeAsync(ProviderType.Claude, "  partial\n");
		Assert.True(verifying.IsActive);

		var rejected = await WaitForAsync(service, ProviderType.Claude, session => session.ErrorMessage != null);
		Assert.Equal(ProviderSignInState.WaitingForUser, rejected.State);
		Assert.Contains("whole code", rejected.ErrorMessage);

		await service.SubmitCodeAsync(ProviderType.Claude, "code#state");
		var finished = await WaitForAsync(service, ProviderType.Claude, session => !session.IsActive);

		Assert.Equal(ProviderSignInState.Succeeded, finished.State);
		Assert.Equal("Login successful.", finished.Message);
		Assert.Null(finished.ErrorMessage);
		await WaitUntilAsync(() => signedIn.Count == 1);
		Assert.Equal(ProviderType.Claude, Assert.Single(signedIn));
	}

	[Fact]
	public async Task CopilotSignIn_ShowsTheDeviceCode_AndFinishesWhenGitHubApproves()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		var approved = Path.Combine(_directory, "approved");
		var script = WriteScript($"""
			echo "To authenticate, visit https://github.com/login/device and enter code 9A80-C0EA"
			echo "Waiting for authorization..."
			echo "Failed to copy to clipboard. Please visit https://github.com/login/device and enter the code 9A80-C0EA manually." >&2
			while [ ! -f "{approved}" ]; do sleep 0.1; done
			echo "Signed in successfully as octocat."
			exit 0
			""");
		using var service = CreateService(script);

		await service.StartAsync(ProviderType.Copilot);
		var waiting = await WaitForAsync(service, ProviderType.Copilot, session => session.State == ProviderSignInState.WaitingForUser);

		Assert.Equal("https://github.com/login/device", waiting.SignInUrl);
		Assert.Equal("9A80-C0EA", waiting.UserCode);
		Assert.False(waiting.NeedsCode);
		Assert.Null(waiting.ErrorMessage);

		// A device sign-in has nothing to paste.
		var pasted = await service.SubmitCodeAsync(ProviderType.Copilot, "anything");
		Assert.Equal(ProviderSignInState.WaitingForUser, pasted.State);

		await File.WriteAllTextAsync(approved, "yes");
		var finished = await WaitForAsync(service, ProviderType.Copilot, session => !session.IsActive);

		Assert.Equal(ProviderSignInState.Succeeded, finished.State);
		Assert.Equal("Signed in successfully as octocat.", finished.Message);
	}

	[Fact]
	public async Task CopilotSignIn_WithoutAKeychain_SaysToSaveATokenInstead()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		var script = WriteScript("""
			echo "To authenticate, visit https://github.com/login/device and enter code 9A80-C0EA"
			echo "Login succeeded, but the token was not saved. Install a system keychain or rerun login and accept plaintext storage." >&2
			exit 1
			""");
		using var service = CreateService(script);

		await service.StartAsync(ProviderType.Copilot);
		var finished = await WaitForAsync(service, ProviderType.Copilot, session => !session.IsActive);

		Assert.Equal(ProviderSignInState.Failed, finished.State);
		Assert.Contains("no keychain", finished.ErrorMessage);
		Assert.Contains("Use an API key", finished.ErrorMessage);
	}

	[Fact]
	public async Task StartingAgain_WhileWaiting_ReturnsTheSameSignIn_AndCancelStopsIt()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		var script = WriteScript("""
			echo "If the browser didn't open, visit: https://claude.com/cai/oauth/authorize?code=true"
			while IFS= read -r line; do :; done
			sleep 60
			""");
		using var service = CreateService(script);

		var first = await service.StartAsync(ProviderType.Claude);
		await WaitForAsync(service, ProviderType.Claude, session => session.State == ProviderSignInState.WaitingForUser);
		var second = await service.StartAsync(ProviderType.Claude);

		Assert.Equal(first.Id, second.Id);

		var cancelled = await service.CancelAsync(ProviderType.Claude);
		Assert.Equal(ProviderSignInState.Cancelled, cancelled!.State);

		// The killed process doesn't turn the cancelled sign-in into a failure.
		await Task.Delay(500);
		var latest = await service.GetAsync(ProviderType.Claude);
		Assert.Equal(ProviderSignInState.Cancelled, latest!.State);

		var restarted = await service.StartAsync(ProviderType.Claude);
		Assert.NotEqual(first.Id, restarted.Id);
		await service.CancelAsync(ProviderType.Claude);
	}

	[Fact]
	public async Task SignInThatNeverFinishes_TimesOut()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		var script = WriteScript("""
			echo "To authenticate, visit https://github.com/login/device and enter code 9A80-C0EA"
			sleep 60
			""");
		using var service = CreateService(script, timeout: TimeSpan.FromSeconds(1));

		await service.StartAsync(ProviderType.Copilot);
		var finished = await WaitForAsync(service, ProviderType.Copilot, session => !session.IsActive);

		Assert.Equal(ProviderSignInState.Failed, finished.State);
		Assert.Contains("timed out", finished.ErrorMessage);
	}

	[Fact]
	public async Task FailedCodeExchange_ReportsTheCliReason()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		var script = WriteScript("""
			echo "If the browser didn't open, visit: https://claude.com/cai/oauth/authorize?code=true"
			read -r line
			echo "Login failed: That code wasn't accepted. Sign in again to get a new code." >&2
			exit 1
			""");
		using var service = CreateService(script);

		await service.StartAsync(ProviderType.Claude);
		await WaitForAsync(service, ProviderType.Claude, session => session.State == ProviderSignInState.WaitingForUser);
		await service.SubmitCodeAsync(ProviderType.Claude, "stale#code");
		var finished = await WaitForAsync(service, ProviderType.Claude, session => !session.IsActive);

		Assert.Equal(ProviderSignInState.Failed, finished.State);
		Assert.Equal("That code wasn't accepted. Sign in again to get a new code.", finished.ErrorMessage);
	}

	[Fact]
	public async Task MissingCli_AndUnsupportedProvider_FailRightAway()
	{
		using var missing = new ProviderSignInService(
			(_, _) => Task.FromResult<string?>(null),
			null,
			NullLogger<ProviderSignInService>.Instance,
			TimeSpan.FromMinutes(1));

		var notInstalled = await missing.StartAsync(ProviderType.Copilot);
		Assert.Equal(ProviderSignInState.Failed, notInstalled.State);
		Assert.Contains("isn't installed", notInstalled.ErrorMessage);

		var unsupported = await missing.StartAsync(ProviderType.OpenCode);
		Assert.Equal(ProviderSignInState.Failed, unsupported.State);
		Assert.Contains("API key", unsupported.ErrorMessage);

		var noSignIn = await missing.SubmitCodeAsync(ProviderType.Claude, "code#state");
		Assert.Equal(ProviderSignInState.Failed, noSignIn.State);
		Assert.Null(await missing.GetAsync(ProviderType.Claude));
	}

	[Fact]
	public void SignInArguments_KeepCopilotOnTheDeviceFlow()
	{
		Assert.Equal(["auth", "login"], ProviderSignInService.GetSignInArguments(ProviderType.Claude));
		Assert.Equal(["login", "--device-code"], ProviderSignInService.GetSignInArguments(ProviderType.Copilot));
	}

	[Fact]
	public void Output_ParsesTheLinesTheClisPrint()
	{
		Assert.Equal("Login successful.", ProviderSignInOutput.Clean("Paste code here if prompted > Login successful."));
		Assert.Equal("Signed in", ProviderSignInOutput.Clean("\u001b[32mSigned in\u001b[0m"));

		Assert.True(ProviderSignInOutput.TryParseDeviceCode(
			"To authenticate, visit https://github.com/login/device and enter code 9A80-C0EA",
			out var deviceUrl,
			out var userCode));
		Assert.Equal("https://github.com/login/device", deviceUrl);
		Assert.Equal("9A80-C0EA", userCode);

		Assert.True(ProviderSignInOutput.TryParseUrl(
			"If the browser didn't open, visit: https://claude.com/cai/oauth/authorize?code=true&scope=user%3Aprofile",
			out var url));
		Assert.Equal("https://claude.com/cai/oauth/authorize?code=true&scope=user%3Aprofile", url);
		Assert.False(ProviderSignInOutput.TryParseUrl("Opening browser to sign in…", out _));

		Assert.Equal(
			"Something broke",
			ProviderSignInOutput.DescribeFailure(
				["Failed to copy to clipboard. Please visit https://github.com/login/device and enter the code X manually.", "Something broke"],
				[],
				1));
		Assert.Equal(
			"The sign-in command stopped with exit code 2.",
			ProviderSignInOutput.DescribeFailure([], ["To authenticate, visit https://github.com/login/device and enter code X", "Waiting for authorization..."], 2));
	}

	public void Dispose()
	{
		try
		{
			Directory.Delete(_directory, recursive: true);
		}
		catch
		{
			// A killed script may still be letting go of the directory.
		}
	}

	private ProviderSignInService CreateService(string executablePath, List<ProviderType>? signedIn = null, TimeSpan? timeout = null)
		=> new(
			(_, _) => Task.FromResult<string?>(executablePath),
			(providerType, _) =>
			{
				if (signedIn != null)
				{
					lock (signedIn)
					{
						signedIn.Add(providerType);
					}
				}

				return Task.CompletedTask;
			},
			NullLogger<ProviderSignInService>.Instance,
			timeout ?? TimeSpan.FromMinutes(1));

	private string WriteScript(string body)
	{
		var path = Path.Combine(_directory, $"fake-cli-{Guid.NewGuid():N}.sh");
		File.WriteAllText(path, "#!/bin/bash\n" + body.Replace("\r\n", "\n") + "\n");
		if (!OperatingSystem.IsWindows())
		{
			File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		}
		return path;
	}

	private static async Task<ProviderSignInSession> WaitForAsync(
		ProviderSignInService service,
		ProviderType providerType,
		Func<ProviderSignInSession, bool> condition)
	{
		var deadline = DateTime.UtcNow + WaitLimit;
		ProviderSignInSession? latest = null;
		while (DateTime.UtcNow < deadline)
		{
			latest = await service.GetAsync(providerType);
			if (latest != null && condition(latest))
			{
				return latest;
			}

			await Task.Delay(50);
		}

		throw new TimeoutException($"Sign-in never reached the expected state; last state {latest?.State}, error {latest?.ErrorMessage}.");
	}

	private static async Task WaitUntilAsync(Func<bool> condition)
	{
		var deadline = DateTime.UtcNow + WaitLimit;
		while (!condition() && DateTime.UtcNow < deadline)
		{
			await Task.Delay(50);
		}
	}
}
