using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Shared.Data;
using VibeSwarm.Web.Pages;

namespace VibeSwarm.Tests;

public sealed class OnboardingPageModelTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly ServiceProvider _serviceProvider;

	public OnboardingPageModelTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();

		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
		services.AddHttpContextAccessor();
		services.AddDbContext<VibeSwarmDbContext>(options => options.UseSqlite(_connection));
		services.AddIdentity<ApplicationUser, IdentityRole<Guid>>()
			.AddEntityFrameworkStores<VibeSwarmDbContext>()
			.AddDefaultTokenProviders();

		_serviceProvider = services.BuildServiceProvider();

		using var scope = _serviceProvider.CreateScope();
		using var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
		dbContext.Database.EnsureCreated();
	}

	[Fact]
	public void LoginModel_OnGet_RedirectsToSetup_WhenNoUsersExist()
	{
		using var scope = _serviceProvider.CreateScope();
		var model = new LoginModel(
			scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>(),
			scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(),
			NullLogger<LoginModel>.Instance)
		{
			PageContext = CreatePageContext(scope.ServiceProvider)
		};

		var result = model.OnGet();

		var redirect = Assert.IsType<RedirectResult>(result);
		Assert.Equal("/setup", redirect.Url);
	}

	[Fact]
	public void SetupModel_OnGet_ReturnsPage_WhenNoUsersExist_EvenIfAdminEnvVarsAreConfigured()
	{
		using var adminUser = new EnvironmentVariableScope("DEFAULT_ADMIN_USER", "admin");
		using var adminPass = new EnvironmentVariableScope("DEFAULT_ADMIN_PASS", "InvalidPassword");
		using var scope = _serviceProvider.CreateScope();
		var model = new SetupModel(
			scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(),
			scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>(),
			scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>(),
			NullLogger<SetupModel>.Instance)
		{
			PageContext = CreatePageContext(scope.ServiceProvider)
		};

		var result = model.OnGet();

		Assert.IsType<PageResult>(result);
	}

	public void Dispose()
	{
		_serviceProvider.Dispose();
		_connection.Dispose();
	}

	private static PageContext CreatePageContext(IServiceProvider serviceProvider)
	{
		return new PageContext
		{
			HttpContext = new DefaultHttpContext
			{
				RequestServices = serviceProvider
			}
		};
	}

	private sealed class EnvironmentVariableScope : IDisposable
	{
		private readonly string _name;
		private readonly string? _originalValue;

		public EnvironmentVariableScope(string name, string? value)
		{
			_name = name;
			_originalValue = Environment.GetEnvironmentVariable(name);
			Environment.SetEnvironmentVariable(name, value);
		}

		public void Dispose()
		{
			Environment.SetEnvironmentVariable(_name, _originalValue);
		}
	}
}
