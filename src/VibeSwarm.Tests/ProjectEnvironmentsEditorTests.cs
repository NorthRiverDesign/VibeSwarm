using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Client.Components.Projects;
using VibeSwarm.Shared.Data;

namespace VibeSwarm.Tests;

public sealed class ProjectEnvironmentsEditorTests
{
	[Fact]
	public async Task RenderedEditorInsideEditForm_WithExistingEnvironment_DoesNotThrow()
	{
		var project = new Project
		{
			Name = "Web App",
			WorkingPath = "/tmp/web-app",
			Environments =
			[
				new ProjectEnvironment
				{
					Id = Guid.NewGuid(),
					Name = "Staging",
					Type = EnvironmentType.Web,
					Stage = EnvironmentStage.Development,
					Url = "https://staging.example.com",
					IsEnabled = true,
					IsPrimary = true
				}
			]
		};

		var services = new ServiceCollection();
		services.AddLogging();

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		var html = await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
			{
				[nameof(EnvironmentEditorHost.Project)] = project
			});
			var output = await renderer.RenderComponentAsync<EnvironmentEditorHost>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("Project Environments", html);
		Assert.Contains("Primary environment", html);
		Assert.Contains("Environment Type", html);
		Assert.Contains("Development", html);
		Assert.Contains("Staging", html);
	}

	[Fact]
	public async Task RenderedEditor_ShowsOptionalCredentialLabels_AndResponsiveActionLayout()
	{
		var project = new Project
		{
			Name = "Web App",
			WorkingPath = "/tmp/web-app",
			Environments =
			[
				new ProjectEnvironment
				{
					Id = Guid.NewGuid(),
					Name = "Staging",
					Type = EnvironmentType.Web,
					Stage = EnvironmentStage.Development,
					Url = "https://staging.example.com",
					IsEnabled = true
				}
			]
		};

		var services = new ServiceCollection();
		services.AddLogging();

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		var html = await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
			{
				[nameof(EnvironmentEditorHost.Project)] = project
			});
			var output = await renderer.RenderComponentAsync<EnvironmentEditorHost>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("Username", html);
		Assert.Contains("Password", html);
		Assert.Contains("(optional)", html);
		Assert.Contains("Username and password are optional.", html);
		Assert.Contains("d-flex flex-wrap justify-content-end gap-2", html);
	}

	private sealed class EnvironmentEditorHost : ComponentBase
	{
		[Parameter, EditorRequired]
		public Project Project { get; set; } = new();

		protected override void BuildRenderTree(RenderTreeBuilder builder)
		{
			builder.OpenComponent<EditForm>(0);
			builder.AddAttribute(1, "Model", Project);
			builder.AddAttribute(2, "ChildContent", (RenderFragment<EditContext>)(_ => contentBuilder =>
			{
				contentBuilder.OpenComponent<ProjectEnvironmentsEditor>(0);
				contentBuilder.AddAttribute(1, nameof(ProjectEnvironmentsEditor.Project), Project);
				contentBuilder.CloseComponent();
			}));
			builder.CloseComponent();
		}
	}
}
