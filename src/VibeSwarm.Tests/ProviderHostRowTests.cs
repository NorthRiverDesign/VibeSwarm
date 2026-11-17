using Bunit;
using Microsoft.Extensions.DependencyInjection;
using VibeSwarm.Client.Services;
using VibeSwarm.Client.Components.Providers;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

public sealed class ProviderHostRowTests
{
	[Fact]
	public void NotInstalled_OpensItselfWithInstallAsThePrimaryAction()
	{
		using var context = new BunitContext();

		var cut = context.Render<ProviderHostRow>(parameters => parameters
			.Add(component => component.Status, CreateStatus(isInstalled: false, isAuthenticated: false)));

		Assert.Contains("Not installed", cut.Markup);
		Assert.Contains("aria-expanded=\"true\"", cut.Markup);
		var install = cut.FindAll("button").Single(button => button.TextContent.Trim() == "Install");
		Assert.Contains("btn-solid theme-primary", install.ClassName);
		Assert.Contains("ripgrep", cut.Markup);
		Assert.Contains("fd / fdfind", cut.Markup);
	}

	[Fact]
	public void SignedOut_SaysWhatToDoOnTheCollapsedRow()
	{
		using var context = new BunitContext();

		var cut = context.Render<ProviderHostRow>(parameters => parameters
			.Add(component => component.Status, CreateStatus(isInstalled: true, isAuthenticated: false)));

		Assert.Contains("Needs sign-in", cut.Markup);

		// Opened automatically; closing it leaves the instruction on the row itself.
		cut.Find("button[aria-expanded]").Click();

		Assert.Contains("aria-expanded=\"false\"", cut.Markup);
		Assert.Contains("Sign in with 'copilot login'", cut.Markup);
	}

	[Fact]
	public void Ready_StaysClosedAndShowsDetailsWhenOpened()
	{
		using var context = new BunitContext();

		var cut = context.Render<ProviderHostRow>(parameters => parameters
			.Add(component => component.Status, CreateStatus(isInstalled: true, isAuthenticated: true)));

		Assert.Contains("Ready", cut.Markup);
		Assert.Contains("aria-expanded=\"false\"", cut.Markup);
		Assert.DoesNotContain("/usr/local/bin/copilot", cut.Markup);

		cut.Find("button[aria-expanded]").Click();

		Assert.Contains("1.0.91", cut.Markup);
		Assert.Contains("/usr/local/bin/copilot", cut.Markup);
		Assert.Contains("Yes · Logged-in User", cut.Markup);
	}

	[Fact]
	public void ApiKeyForm_RequiresAKeyBeforeSaving()
	{
		using var context = new BunitContext();
		CommonProviderSetupRequest? saved = null;

		var cut = context.Render<ProviderHostRow>(parameters => parameters
			.Add(component => component.Status, CreateStatus(isInstalled: true, isAuthenticated: false))
			.Add(component => component.OnSaveAuthentication, request => saved = request));

		cut.FindAll("button").Single(button => button.TextContent.Trim() == "Use an API key").Click();
		cut.Find("form").Submit();

		Assert.Null(saved);
		Assert.Contains("GitHub Token is required.", cut.Markup);

		cut.Find("input[type=password]").Change("ghp_example");
		cut.Find("form").Submit();

		Assert.NotNull(saved);
		Assert.Equal(ProviderType.Copilot, saved!.ProviderType);
		Assert.Equal("ghp_example", saved.ApiKey);
	}

	[Fact]
	public void SignedOut_OffersSigningInAsTheMainAction()
	{
		using var context = new BunitContext();

		var cut = context.Render<ProviderHostRow>(parameters => parameters
			.Add(component => component.Status, CreateStatus(isInstalled: true, isAuthenticated: false)));

		var signIn = cut.FindAll("button").Single(button => button.TextContent.Trim() == "Sign in");
		Assert.Contains("theme-primary", signIn.ClassName);
		var apiKey = cut.FindAll("button").Single(button => button.TextContent.Trim() == "Use an API key");
		Assert.Contains("theme-secondary", apiKey.ClassName);
	}

	[Fact]
	public void SuggestedSignIn_OpensAReadyRowOnSigningInAgain()
	{
		using var context = new BunitContext();

		var cut = context.Render<ProviderHostRow>(parameters => parameters
			.Add(component => component.Status, CreateStatus(isInstalled: true, isAuthenticated: true))
			.Add(component => component.SuggestSignIn, true));

		Assert.Contains("aria-expanded=\"true\"", cut.Markup);
		var signIn = cut.FindAll("button").Single(button => button.TextContent.Trim() == "Sign in again");
		Assert.Contains("theme-primary", signIn.ClassName);
	}

	[Fact]
	public void SignIn_ShowsGitHubsDeviceCodeAndLink()
	{
		using var context = new BunitContext();
		context.JSInterop.Mode = JSRuntimeMode.Loose;
		var signInService = new FakeSignInService
		{
			Started = new ProviderSignInSession
			{
				Id = Guid.NewGuid(),
				ProviderType = ProviderType.Copilot,
				State = ProviderSignInState.WaitingForUser,
				SignInUrl = "https://github.com/login/device",
				UserCode = "9A80-C0EA"
			}
		};
		context.Services.AddSingleton<IProviderSignInService>(signInService);
		context.Services.AddSingleton<NotificationService>();

		var cut = context.Render<ProviderHostRow>(parameters => parameters
			.Add(component => component.Status, CreateStatus(isInstalled: true, isAuthenticated: false)));

		cut.FindAll("button").Single(button => button.TextContent.Trim() == "Sign in").Click();

		cut.WaitForAssertion(() => Assert.Contains("9A80-C0EA", cut.Markup));
		Assert.Equal(1, signInService.StartCount);
		Assert.Equal("https://github.com/login/device", cut.Find("a[target=_blank]").GetAttribute("href"));
		Assert.Contains("Open GitHub", cut.Markup);
		Assert.Empty(cut.FindAll("input"));
	}

	[Fact]
	public void PendingSignIn_ReopensWithThePasteBox_AndSendsTheCode()
	{
		using var context = new BunitContext();
		var pending = new ProviderSignInSession
		{
			Id = Guid.NewGuid(),
			ProviderType = ProviderType.Claude,
			State = ProviderSignInState.WaitingForUser,
			SignInUrl = "https://claude.com/cai/oauth/authorize?code=true",
			NeedsCode = true
		};
		var signInService = new FakeSignInService { Latest = pending };
		context.Services.AddSingleton<IProviderSignInService>(signInService);

		var status = CreateStatus(isInstalled: true, isAuthenticated: false);
		status.ProviderType = ProviderType.Claude;
		status.DisplayName = "Claude Code";
		status.ActiveSignIn = pending;

		var cut = context.Render<ProviderHostRow>(parameters => parameters.Add(component => component.Status, status));

		Assert.Contains("Open sign-in page", cut.Markup);
		Assert.Equal(0, signInService.StartCount);

		cut.Find("input").Input("code#state");
		cut.Find("form").Submit();

		cut.WaitForAssertion(() => Assert.Equal("code#state", signInService.SubmittedCode));
	}

	[Fact]
	public void CancellingAPendingSignIn_ThenSigningIn_StartsAFreshOne()
	{
		using var context = new BunitContext();
		var pending = new ProviderSignInSession
		{
			Id = Guid.NewGuid(),
			ProviderType = ProviderType.Claude,
			State = ProviderSignInState.WaitingForUser,
			SignInUrl = "https://claude.com/cai/oauth/authorize?code=old",
			NeedsCode = true
		};
		var signInService = new FakeSignInService
		{
			Latest = pending,
			Started = new ProviderSignInSession
			{
				Id = Guid.NewGuid(),
				ProviderType = ProviderType.Claude,
				State = ProviderSignInState.WaitingForUser,
				SignInUrl = "https://claude.com/cai/oauth/authorize?code=new",
				NeedsCode = true
			}
		};
		context.Services.AddSingleton<IProviderSignInService>(signInService);

		var status = CreateStatus(isInstalled: true, isAuthenticated: false);
		status.ProviderType = ProviderType.Claude;
		status.ActiveSignIn = pending;

		var cut = context.Render<ProviderHostRow>(parameters => parameters.Add(component => component.Status, status));

		cut.FindAll("button").Single(button => button.TextContent.Trim() == "Cancel").Click();
		cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("button"), button => button.TextContent.Trim() == "Sign in"));
		cut.FindAll("button").Single(button => button.TextContent.Trim() == "Sign in").Click();

		cut.WaitForAssertion(() => Assert.Equal(1, signInService.StartCount));
		cut.WaitForAssertion(() => Assert.Equal("https://claude.com/cai/oauth/authorize?code=new", cut.Find("a[target=_blank]").GetAttribute("href")));
	}

	[Fact]
	public void SignInPanel_ReportsASuccessfulSignIn()
	{
		using var context = new BunitContext();
		var id = Guid.NewGuid();
		var signInService = new FakeSignInService
		{
			Started = new ProviderSignInSession { Id = id, ProviderType = ProviderType.Copilot, State = ProviderSignInState.Starting },
			Latest = new ProviderSignInSession
			{
				Id = id,
				ProviderType = ProviderType.Copilot,
				State = ProviderSignInState.Succeeded,
				Message = "Signed in successfully as octocat."
			}
		};
		context.Services.AddSingleton<IProviderSignInService>(signInService);
		ProviderSignInSession? signedIn = null;

		var cut = context.Render<ProviderSignInPanel>(parameters => parameters
			.Add(component => component.ProviderType, ProviderType.Copilot)
			.Add(component => component.PollInterval, TimeSpan.FromMilliseconds(20))
			.Add(component => component.OnSignedIn, session => signedIn = session));

		cut.WaitForAssertion(() => Assert.Equal("Signed in successfully as octocat.", signedIn?.Message));
	}

	private sealed class FakeSignInService : IProviderSignInService
	{
		public ProviderSignInSession? Started { get; init; }
		public ProviderSignInSession? Latest { get; set; }
		public int StartCount { get; private set; }
		public string? SubmittedCode { get; private set; }

		public Task<ProviderSignInSession> StartAsync(ProviderType providerType, CancellationToken cancellationToken = default)
		{
			StartCount++;
			return Task.FromResult(Started ?? throw new InvalidOperationException("No sign-in to start."));
		}

		public Task<ProviderSignInSession?> GetAsync(ProviderType providerType, CancellationToken cancellationToken = default)
			=> Task.FromResult(Latest ?? Started);

		public Task<ProviderSignInSession> SubmitCodeAsync(ProviderType providerType, string code, CancellationToken cancellationToken = default)
		{
			SubmittedCode = code;
			var session = Latest!;
			return Task.FromResult(new ProviderSignInSession
			{
				Id = session.Id,
				ProviderType = providerType,
				State = ProviderSignInState.Verifying,
				SignInUrl = session.SignInUrl,
				NeedsCode = true
			});
		}

		public Task<ProviderSignInSession?> CancelAsync(ProviderType providerType, CancellationToken cancellationToken = default)
			=> Task.FromResult<ProviderSignInSession?>(null);
	}

	private static CommonProviderSetupStatus CreateStatus(bool isInstalled, bool isAuthenticated) => new()
	{
		ProviderType = ProviderType.Copilot,
		DisplayName = "GitHub Copilot",
		DocumentationUrl = "https://docs.github.com/copilot",
		ApiKeyLabel = "GitHub Token",
		ApiKeyHelpText = "Use a fine-grained PAT.",
		IsInstalled = isInstalled,
		InstalledVersion = isInstalled ? "1.0.91" : null,
		ResolvedExecutablePath = isInstalled ? "/usr/local/bin/copilot" : null,
		AuthenticationConnectionMode = ProviderConnectionMode.CLI,
		AuthenticationTypeLabel = "Logged-in User",
		IsAuthenticated = isAuthenticated,
		AuthenticationStatus = isAuthenticated
			? "Copilot CLI login detected on host for this CLI connection."
			: "Sign in with 'copilot login' or save a GitHub token for this CLI connection.",
		HostTools =
		[
			new CommonProviderHostToolStatus { Name = "ripgrep", Command = "rg", Purpose = "Fast content search.", IsInstalled = true },
			new CommonProviderHostToolStatus { Name = "fd", Command = "fd / fdfind", Purpose = "Fast file-name search.", IsInstalled = false }
		]
	};
}
