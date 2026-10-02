using Bunit;
using Microsoft.AspNetCore.Components;
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

		using var context = new BunitContext();
		var cut = RenderUsersPage(context, [activeUser, inactiveUser]);

		cut.WaitForAssertion(() => Assert.Contains("aria-label=\"Add a user\"", cut.Markup));
		var html = cut.Markup;

		Assert.Contains(">Users<", html);
		Assert.Contains("aria-label=\"Add a user\"", html);
		Assert.Contains("btn btn-primary", html);
	}

	[Fact]
	public void UserListSection_GroupsUsersWhoCanSignInAboveSwitchedOffOnes()
	{
		using var context = new BunitContext();

		var cut = context.Render<UserListSection>(parameters => parameters
			.Add(component => component.Users, BuildUsers())
			.Add(component => component.CurrentUserId, Guid.NewGuid()));

		var html = cut.Markup;

		Assert.DoesNotContain("nav-tabs", html);
		Assert.Contains("Can sign in", html);
		Assert.Contains("Switched off", html);
		Assert.True(html.IndexOf("alice", StringComparison.Ordinal) < html.IndexOf("Switched off", StringComparison.Ordinal));
		Assert.True(html.IndexOf("Switched off", StringComparison.Ordinal) < html.IndexOf("bob", StringComparison.Ordinal));
		Assert.Contains("Admin", html);
	}

	[Fact]
	public void UserRow_OpensToAccountActions()
	{
		using var context = new BunitContext();
		var user = BuildUsers()[0];
		UserDto? reset = null;

		var cut = context.Render<UserRow>(parameters => parameters
			.Add(component => component.User, user)
			.Add(component => component.CurrentUserId, Guid.NewGuid())
			.Add(component => component.OnResetPassword, u => reset = u));

		Assert.DoesNotContain("Reset password", cut.Markup);

		cut.Find("button[aria-expanded]").Click();
		cut.FindAll("button").Single(button => button.TextContent.Trim() == "Reset password").Click();

		Assert.Same(user, reset);
		Assert.Contains("Change role", cut.Markup);
		Assert.Contains("Delete", cut.Markup);
	}

	[Fact]
	public void UserRow_DoesNotOfferToSwitchOffOrDeleteYourself()
	{
		using var context = new BunitContext();
		var user = BuildUsers()[0];

		var cut = context.Render<UserRow>(parameters => parameters
			.Add(component => component.User, user)
			.Add(component => component.CurrentUserId, user.Id));

		Assert.Contains(">You<", cut.Markup);

		cut.Find("button[aria-expanded]").Click();

		Assert.True(cut.Find("input[role=switch]").HasAttribute("disabled"));
		Assert.DoesNotContain("bi-trash", cut.Markup);
	}

	[Fact]
	public void RenderedUsersPage_ShowsEmptyStateWhenNoUsersExist()
	{
		using var context = new BunitContext();
		var cut = RenderUsersPage(context, []);

		cut.WaitForAssertion(() => Assert.Contains("No users found", cut.Markup));
	}

	[Fact]
	public void RenderedUsersPage_ListsEveryUserWithoutFiltering()
	{
		using var context = new BunitContext();
		var cut = RenderUsersPage(context, BuildUsers());

		cut.WaitForAssertion(() => Assert.Contains("alice", cut.Markup));

		Assert.Contains("bob", cut.Markup);
	}

	[Fact]
	public void RenderedUsersPage_DoesNotLeakUnboundParameterLiterals()
	{
		using var context = new BunitContext();
		var cut = RenderUsersPage(context, BuildUsers());

		cut.WaitForAssertion(() => Assert.Contains("alice", cut.Markup));

		// A missing '@' on a string parameter shows up as the field name in the DOM.
		Assert.DoesNotContain("_activeFilter", cut.Markup);
		Assert.DoesNotContain("_addUserError", cut.Markup);
	}

	private static List<UserDto> BuildUsers() =>
	[
		new()
		{
			Id = Guid.NewGuid(),
			UserName = "alice",
			IsActive = true,
			CreatedAt = DateTime.UtcNow,
			Roles = [UserRoles.Admin]
		},
		new()
		{
			Id = Guid.NewGuid(),
			UserName = "bob",
			IsActive = false,
			CreatedAt = DateTime.UtcNow,
			Roles = [UserRoles.User]
		}
	];

	private static IRenderedComponent<Users> RenderUsersPage(BunitContext context, IReadOnlyList<UserDto> users)
	{
		context.Services.AddSingleton<IUserService>(new FakeUserService(users));
		context.Services.AddSingleton<NotificationService>();

		return context.Render<Users>();
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
