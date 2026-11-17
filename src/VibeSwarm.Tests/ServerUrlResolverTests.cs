using Microsoft.AspNetCore.Http;
using VibeSwarm.Web;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class ServerUrlResolverTests
{
	[Fact]
	public void Resolve_WithoutConfiguredUrls_ServesHttpOnlyByDefault()
	{
		var resolution = ServerUrlResolver.Resolve(null, enableHttps: false);

		Assert.Equal([ServerUrlResolver.DefaultHttpUrl], resolution.Urls);
		Assert.False(resolution.ServesHttps);
		Assert.Null(resolution.Message);
	}

	[Fact]
	public void Resolve_WithoutConfiguredUrls_AddsDefaultHttpsUrlWhenEnabled()
	{
		var resolution = ServerUrlResolver.Resolve("", enableHttps: true);

		Assert.Equal([ServerUrlResolver.DefaultHttpUrl, ServerUrlResolver.DefaultHttpsUrl], resolution.Urls);
		Assert.True(resolution.ServesHttps);
	}

	[Fact]
	public void Resolve_HttpsDisabled_DropsConfiguredHttpsUrls()
	{
		var resolution = ServerUrlResolver.Resolve("https://0.0.0.0:5001; http://0.0.0.0:5000;", enableHttps: false);

		Assert.Equal(["http://0.0.0.0:5000"], resolution.Urls);
		Assert.False(resolution.ServesHttps);
		Assert.Contains("https://0.0.0.0:5001", resolution.Message);
	}

	[Fact]
	public void Resolve_HttpsEnabled_KeepsEveryConfiguredUrl()
	{
		var resolution = ServerUrlResolver.Resolve("https://0.0.0.0:5001;http://0.0.0.0:5000", enableHttps: true);

		Assert.Equal(["https://0.0.0.0:5001", "http://0.0.0.0:5000"], resolution.Urls);
		Assert.True(resolution.ServesHttps);
		Assert.Null(resolution.Message);
	}

	[Fact]
	public void Resolve_HttpsDisabledWithOnlyHttpsUrls_KeepsThemRatherThanBindingNothing()
	{
		var resolution = ServerUrlResolver.Resolve("HTTPS://0.0.0.0:5001", enableHttps: false);

		Assert.Equal(["HTTPS://0.0.0.0:5001"], resolution.Urls);
		Assert.True(resolution.ServesHttps);
		Assert.Contains("HTTPS stays on", resolution.Message);
	}

	[Fact]
	public void Resolve_HttpsEnabledWithOnlyHttpUrls_WarnsThatHttpsIsNotServed()
	{
		var resolution = ServerUrlResolver.Resolve("http://0.0.0.0:5000", enableHttps: true);

		Assert.Equal(["http://0.0.0.0:5000"], resolution.Urls);
		Assert.False(resolution.ServesHttps);
		Assert.Contains("no https:// address", resolution.Message);
	}

	[Fact]
	public void SchemeScopedCookieManager_UsesSeparateCookieNameOverHttp()
	{
		var manager = new SchemeScopedCookieManager();
		var httpContext = new DefaultHttpContext();
		httpContext.Request.Scheme = "http";
		httpContext.Request.Headers.Cookie = "VibeSwarm.Auth=secure-ticket; VibeSwarm.Auth.Http=http-ticket";

		manager.AppendResponseCookie(httpContext, "VibeSwarm.Auth", "new-ticket", new CookieOptions());

		Assert.Equal("http-ticket", manager.GetRequestCookie(httpContext, "VibeSwarm.Auth"));
		Assert.StartsWith("VibeSwarm.Auth.Http=new-ticket", httpContext.Response.Headers.SetCookie.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public void SchemeScopedCookieManager_KeepsConfiguredCookieNameOverHttps()
	{
		var manager = new SchemeScopedCookieManager();
		var httpContext = new DefaultHttpContext();
		httpContext.Request.Scheme = "https";
		httpContext.Request.Headers.Cookie = "VibeSwarm.Auth=secure-ticket; VibeSwarm.Auth.Http=http-ticket";

		manager.DeleteCookie(httpContext, "VibeSwarm.Auth", new CookieOptions());

		Assert.Equal("secure-ticket", manager.GetRequestCookie(httpContext, "VibeSwarm.Auth"));
		Assert.StartsWith("VibeSwarm.Auth=;", httpContext.Response.Headers.SetCookie.ToString(), StringComparison.Ordinal);
	}
}
