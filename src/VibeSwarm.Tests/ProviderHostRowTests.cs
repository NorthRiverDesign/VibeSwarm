using Bunit;
using VibeSwarm.Client.Components.Providers;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;

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
		Assert.Contains("btn-primary", install.ClassName);
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
