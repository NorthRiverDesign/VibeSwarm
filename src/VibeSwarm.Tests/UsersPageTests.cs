using Bunit;
using Microsoft.Extensions.DependencyInjection;
using VibeSwarm.Client.Components.Users;
using VibeSwarm.Client.Pages;
using VibeSwarm.Client.Services;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

public sealed class UsersPageTests
{
	[Fact]
	public void RenderedUsersPage_ShowsHeaderAndPrimaryAction()
	{
		var activeUser = new UserDto
		{
			Id = Guid.NewGuid(),
			UserName = "alice",
			IsActive = true,
			CreatedAt = DateTime.UtcNow,
			Roles = [UserRoles.Admin]
		};
		var inactiveUser = new UserDto
		{
			Id = Guid.NewGuid(),
			UserName = "bob",
			IsActive = false,
			CreatedAt = DateTime.UtcNow,
			Roles = [UserRoles.User]
		};

		var html = RenderUsersPage([activeUser, inactiveUser]);

		Assert.Contains("Manage user accounts and permissions", html);
		Assert.Contains("Add User", html);
		Assert.Contains("btn btn-primary", html);
	}

	[Fact]
	public void UserFilterTabs_RendersCountsAndActiveState()
	{
		using var context = new BunitContext();

		var cut = context.Render<UserFilterTabs>(parameters => parameters
			.Add(component => component.ActiveFilter, "active")
			.Add(component => component.ActiveCount, 3)
			.Add(component => component.InactiveCount, 1));

		var html = cut.Markup;

		Assert.Contains("overflow-x-auto overflow-y-hidden flex-nowrap", html);
		Assert.Contains("Active", html);
		Assert.Contains("Inactive", html);
		Assert.Contains(">3<", html);
		Assert.Contains(">1<", html);
		Assert.Contains("nav-link active", html);
	}

	[Fact]
	public void UserListSection_RendersActiveUsersAndActions()
	{
		using var context = new BunitContext();

		var activeUser = new UserDto
		{
			Id = Guid.NewGuid(),
			UserName = "alice",
			IsActive = true,
			CreatedAt = DateTime.UtcNow,
			Roles = [UserRoles.Admin]
		};
		var inactiveUser = new UserDto
		{
			Id = Guid.NewGuid(),
			UserName = "bob",
			IsActive = false,
			CreatedAt = DateTime.UtcNow,
			Roles = [UserRoles.User]
		};

		var cut = context.Render<UserListSection>(parameters => parameters
			.Add(component => component.Users, [activeUser, inactiveUser])
			.Add(component => component.ActiveFilter, "active")
			.Add(component => component.CurrentUserId, Guid.NewGuid()));

		var html = cut.Markup;

		Assert.Contains("alice", html);
		Assert.Contains("Admin", html);
		Assert.Contains("Reset Password", html);
		Assert.Contains("Delete", html);
		Assert.DoesNotContain("bob", html);
	}

	[Fact]
	public void RenderedUsersPage_ShowsEmptyStateWhenNoUsersExist()
	{
		var html = RenderUsersPage([]);

		Assert.Contains("No users found", html);
		Assert.Contains("Active", html);
		Assert.Contains("Inactive", html);
	}

	private static string RenderUsersPage(IReadOnlyList<UserDto> users)
	{
		using var context = new BunitContext();
		context.Services.AddSingleton<IUserService>(new FakeUserService(users));
		context.Services.AddSingleton<NotificationService>();

		var cut = context.Render<Users>();
		return cut.Markup;
	}

	private sealed class FakeUserService(IReadOnlyList<UserDto> users) : IUserService
	{
		private readonly IReadOnlyList<UserDto> _users = users;

		public Task<IEnumerable<UserDto>> GetAllUsersAsync() => Task.FromResult<IEnumerable<UserDto>>(_users);
		public Task<UserDto?> GetUserByIdAsync(Guid id) => Task.FromResult(_users.FirstOrDefault(user => user.Id == id));
		public Task<(bool Success, string? Error, UserDto? User)> CreateUserAsync(CreateUserModel model) => throw new NotSupportedException();
		public Task<(bool Success, string? Error)> UpdateUserAsync(Guid id, UpdateUserModel model) => throw new NotSupportedException();
		public Task<(bool Success, string? Error)> ResetPasswordAsync(Guid id, string newPassword) => throw new NotSupportedException();
		public Task<(bool Success, string? Error)> DeleteUserAsync(Guid id) => throw new NotSupportedException();
		public Task<(bool Success, string? Error)> ToggleUserActiveAsync(Guid id, Guid currentUserId) => throw new NotSupportedException();
		public Task<IEnumerable<string>> GetUserRolesAsync(Guid id) => Task.FromResult<IEnumerable<string>>([]);
	}
}
