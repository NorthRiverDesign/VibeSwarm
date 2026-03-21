using System.Net;
using System.Text;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Client.Auth;
using VibeSwarm.Client.Components.Common;

namespace VibeSwarm.Tests;

public sealed class CookieAuthenticationStateProviderTests
{
	[Fact]
	public async Task GetAuthenticationStateAsync_PreservesCachedAuthenticatedState_WhenRequestThrows()
	{
		var callCount = 0;
		var provider = CreateProvider((_, _) =>
		{
			callCount++;
			return callCount == 1
				? Task.FromResult(CreateAuthenticatedResponse("kyle"))
				: throw new HttpRequestException("Temporary network issue.");
		});

		var initialState = await provider.GetAuthenticationStateAsync();
		var recoveredState = await provider.GetAuthenticationStateAsync();

		Assert.True(initialState.User.Identity?.IsAuthenticated);
		Assert.True(recoveredState.User.Identity?.IsAuthenticated);
		Assert.Equal("kyle", recoveredState.User.Identity?.Name);
	}

	[Fact]
	public async Task GetAuthenticationStateAsync_ReturnsAnonymous_WhenEndpointReturnsUnauthorized()
	{
		var callCount = 0;
		var provider = CreateProvider((_, _) =>
		{
			callCount++;
			return Task.FromResult(callCount == 1
				? CreateAuthenticatedResponse("kyle")
				: new HttpResponseMessage(HttpStatusCode.Unauthorized));
		});

		var initialState = await provider.GetAuthenticationStateAsync();
		var unauthorizedState = await provider.GetAuthenticationStateAsync();

		Assert.True(initialState.User.Identity?.IsAuthenticated);
		Assert.False(unauthorizedState.User.Identity?.IsAuthenticated ?? false);
	}

	[Fact]
	public void RedirectToLogin_DoesNotNavigate_WhenRefreshRestoresAuthentication()
	{
		using var context = new BunitContext();
		var navigationManager = new TestNavigationManager("http://localhost/jobs/123");
		var provider = CreateProvider((_, _) => Task.FromResult(CreateAuthenticatedResponse("kyle")));

		context.Services.AddSingleton<NavigationManager>(navigationManager);
		context.Services.AddSingleton(provider);

		var cut = context.Render<RedirectToLogin>();

		cut.WaitForAssertion(() => Assert.Equal("http://localhost/jobs/123", navigationManager.Uri));
	}

	[Fact]
	public void RedirectToLogin_NavigatesToLogin_WhenRefreshIsUnauthorized()
	{
		using var context = new BunitContext();
		var navigationManager = new TestNavigationManager("http://localhost/jobs/123");
		var provider = CreateProvider((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));

		context.Services.AddSingleton<NavigationManager>(navigationManager);
		context.Services.AddSingleton(provider);

		var cut = context.Render<RedirectToLogin>();

		cut.WaitForAssertion(() => Assert.Equal("http://localhost/login", navigationManager.Uri));
	}

	private static CookieAuthenticationStateProvider CreateProvider(
		Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
	{
		var httpClient = new HttpClient(new StubHttpMessageHandler(handler))
		{
			BaseAddress = new Uri("http://localhost/")
		};

		return new CookieAuthenticationStateProvider(httpClient, NullLogger<CookieAuthenticationStateProvider>.Instance);
	}

	private static HttpResponseMessage CreateAuthenticatedResponse(string userName)
	{
		return new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent($$"""
			{
				"UserId": "{{Guid.NewGuid()}}",
				"UserName": "{{userName}}",
				"Email": "{{userName}}@example.com",
				"Roles": ["Admin"]
			}
			""", Encoding.UTF8, "application/json")
		};
	}

	private sealed class StubHttpMessageHandler : HttpMessageHandler
	{
		private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

		public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
		{
			_handler = handler;
		}

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
			=> _handler(request, cancellationToken);
	}

	private sealed class TestNavigationManager : NavigationManager
	{
		public TestNavigationManager(string currentUri)
		{
			Initialize("http://localhost/", currentUri);
		}

		protected override void NavigateToCore(string uri, bool forceLoad)
		{
			Uri = ToAbsoluteUri(uri).ToString();
		}
	}
}
