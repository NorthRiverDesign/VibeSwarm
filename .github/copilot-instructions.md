# VibeSwarm

VibeSwarm is an AI coding agent orchestrator that leverages multiple AI models to assist developers in writing, reviewing, and optimizing code. It integrates with various AI services to provide a seamless coding experience. VibeSwarm is intended to be hosted on a VPS, local machine, Raspberry Pi, or cloud instance. VibeSwarm exposes a web interface for managing multiple projects that are able to be autonomously coded by a swarm of AI agents.

VibeSwarm is written in C# using .NET 10 and Blazor.

## Requirements

A user of VibeSwarm is expected to have accounts with various AI providers and have the necessary API keys or authentication tokens available for use by VibeSwarm. The user is also expected to have a basic understanding of how to set up and configure AI coding agents on their host system.

## Features

Projects are set up in VibeSwarm which define a code directory. A project may have a single Job running at a time to avoid code conflicts. Multiple jobs can be queued to create features in a sequential manner.

## Agent Management

VibeSwarm attempts to use various CLI agent providers together. The primary providers are:

- GitHub Copilot CLI
- Claude Code
- OpenCode

Assume the user has already set up and configured these agents tools. Use their own documentation to manage provider service code and use appropriate flags. VibeSwarm should attempt to provide as many flags as possible to map application intent to provider tooling.

## Cross Platform

1. The application must work identically on Windows, Linux and Mac.
2. Users may deploy to a VPS, Raspberry Pi, their own workstation, a random computer, a toaster, etc.
3. Utilize .NET's cross platform capabilities to their fullest, with minimal platform specific code.

## Deployment

1. This application calls host system CLI tools.
2. Avoid Docker for now, as CLI tools would have to be configured inside a container to work properly.
3. A simple `dotnet publish ...` should handle the entire build.

## VibeSwarm Architecture

1. The application is in 3 parts. A web UI, a Shared project for reusing code, and a worker process that manages CLI processes.
2. If code is used by the Web UI and Worker process, it should live in the Shared project.

## Coding Best Practices

VibeSwarm follows coding best practices to ensure maintainability, readability, and performance. This includes adhering to established coding standards, using meaningful variable and function names, and writing modular code. The application is structured to promote separation of concerns, with distinct layers for data access, business logic, and presentation.

Because VibeSwarm interacts with rapidly changing AI agents and services, the codebase is designed to be flexible and adaptable. This includes using interfaces and abstractions to allow for easy integration of new agents and providers as they become available. Service provider implementations should be encapsulated to minimize the impact of changes on the overall application.

The C# coding style follows the official Microsoft C# coding conventions. Consistent formatting, indentation, and spacing are used throughout the codebase to enhance readability. Comments and documentation are provided where necessary to explain complex logic and provide context for future developers.

## Usage Limits for AI Agents

1. Attempt to detect and respect usage limits of providers. Some like Copilot use premium requests, and others like Claude use a token limit. The application should see these as a percentage of utilized resources.
2. Always avoid going over-limit with a provider if the information is available. Some provides like OpenCode may use local models where a usage limit does not exist.
3. Do not spam providers. If a provider has a known rate limit, always wait the appropriate amount of time before sending another request. Even with jobs, ensure there is a delay between executions to avoid overwhelming the provider.

## Cost Management

The user may or may not be billed based on provider usage. All attempts must be made to relay usage statistics back to the application and in a format that allows stop signals and/or circuit breakers. The user must remain fully in control of what limits they allow VibeSwarm to delegate to CLI agents, even if there is work to do in the application.

## Fresh Context Windows

VibeSwarm always starts jobs with a fresh context window and/or session on the CLI provider. The session ID through the provider (if available) is stored to facilitate resuming operations in the event of an error. Always attempt to give Jobs the best possible chance of succeeding according to user intent.

## Agent Skills

VibeSwarm has a Skills feature where instructions may be present for specific tasks across various projects. Always attempt to expose these to a CLI agent in order to get the best possible outcome.

## User Interface

1. The application front-end is based on Bootstrap v5.x.
2. Stick to what the framework provides as much as possible.
3. Always ensure a consistent look and feel across the entire application.
4. Make sure the application is fully responsive and mobile-friendly.
5. Ensure consistent spacing, alignment, and visual hierarchy throughout the application. Use the framework and its utility classes to achieve this.
6. Create reusable UI components for common elements such as buttons, forms, modals, and cards to promote consistency and reduce code duplication.
7. Use a Tailwind-approach of stacking utility classes to achieve complex layouts and designs without custom CSS.
8. Ensure all UI components are accessible, following WCAG guidelines to provide an inclusive experience for all users.

### Mobile Friendly

VibeSwarm is designed to be mobile-friendly, ensuring that users can access and manage their projects from smartphones and tablets. The responsive design adapts to various screen sizes, providing an optimal user experience regardless of the device being used. The mobile interface includes touch-friendly elements and simplified navigation to facilitate ease of use on smaller screens.

All pages and UI components are tested on an iPhone to ensure every aspect of the application works from anywhere the application is accessible. A best attempt is made to ensure the viewport is optimized for mobile devices and desktop devices alike with proper spacing, padding, and alignment.

### Desktop Friendly

The UI should also be optimized for desktop use, taking advantage of larger screen real estate to provide a more detailed and comprehensive view of projects and agent activities. The desktop interface includes additional features and information that may not be necessary on mobile devices, enhancing the overall user experience for desktop users.

Consider the entire viewport when designing layouts, ensuring that content is well-organized and easily accessible on larger screens. Utilize grid systems and flexible layouts to create a visually appealing and functional desktop interface. Ensure there are no gaps of unused space on larger screens by expanding content areas or adding supplementary information where appropriate.

Elements such as Modals should expand to a reasonable size on desktop screens to improve usability, while still being fully functional on mobile devices.

### Bootstrap Integration

Always attempt to leverage Bootstrap's built-in classes and components. Avoid custom styles at all costs, unless absolutely necessary. Custom classes should ONLY exist when there is no equivalent in Bootstrap and should be named using a clear and consistent naming convention. This ensures consistency across the application and reduces the need for additional CSS. Use a stacked utility class approach when creating UI components to maximize flexibility and maintainability. A component may use multiple classes such as `d-flex`, `flex-column`, `align-items-center`, `bg-body-secondary`, and `p-3` to achieve the desired layout and styling without custom CSS. Use the TailwindCSS mindset when applying Bootstrap utility classes to create complex layouts and designs.

Always favor Bootstrap components such as Cards, Modals, Buttons, and Forms to maintain a consistent look and feel throughout the application. Customize these components using Bootstrap's utility classes rather than creating new styles.

### Site.css

The application specific `site.css` should only add helper utilities that can be used across the application, and are not intended to be specific to components.

No components should need a `*.razor.css` file. All styling should be achievable via Bootstrap utility classes alone. If a component requires specific styling that cannot be achieved with Bootstrap classes, consider refactoring the component to better align with Bootstrap's capabilities.

The `site.css` should never contain specific component styles. It should only contain helper classes that can be used across multiple components. Razor components should stack Bootstrap utility classes to achieve the desired styling and layout.

There may be rare cases where a custom class is necessary for a specific component due to limitations in Bootstrap. In such cases, the custom class should be clearly named to indicate its purpose and should be documented within the `site.css` file for clarity. The custom classes should aim to only override properties where a utility class does not exist in Bootstrap.

For any reference, see the official Boostrap utility classes documentation: `https://getbootstrap.com/docs/5.3/utilities/`. There are sub-sections for various utility types such as spacing, flexbox, colors, and more. Always prefer these built-in classes over custom styles.

### Bootstrap Icons

VibeSwarm uses Bootstrap Icons for visual enhancements and to improve user experience. Icons are used throughout the interface to represent actions, statuses, and navigation elements. The application leverages the extensive library of Bootstrap Icons to maintain a consistent look and feel.

When adding icons, prefer using Bootstrap Icons over custom SVGs or other icon libraries to ensure visual consistency. Icons should be appropriately sized and aligned within UI components to enhance usability without overwhelming the design.

Icon usage in buttons should always contain a Title attribute for accessibility and better user experience. This provides additional context for users, especially those using screen readers.

### Blazor Best Practices

1. Use dependency injection to manage services and promote loose coupling between components.
2. Leverage Blazor's built-in state management features to maintain application state across components.
3. Use event callbacks to handle user interactions and communicate between parent and child components.
4. Optimize component rendering by using `ShouldRender` method to prevent unnecessary re-renders.
5. Use asynchronous programming patterns (async/await) to improve application responsiveness.
6. Always consider PWA usage when designing components and pages, ensuring offline capabilities and efficient resource loading.

### UI Patterns

1. Always favor components over large pages.
2. Use Bootstrap utility classes to achieve layout and styling without custom CSS.
3. Keep components under 300 lines of markup and code combined.
4. Use try/catch blocks for data fetching methods to handle errors gracefully.
5. Display loading indicators during asynchronous operations to enhance user experience.
6. Ensure all UI components are responsive and mobile-friendly.
7. The application must function on every screen size without layout issues or unused space.
8. Make common components under `src/VibeSwarm.Web/Components/Common` and reuse them wherever possible.
9. Avoid code-behind files for Razor components; keep logic within the `.razor` file itself.
10. Use SignalR for real-time updates and avoid full page reloads or navigations.
11. Never use tables for layout purposes; reserve tables for tabular data only.

### Toast Messages

Favor using toast messages instead of Flash messages for user notifications. Toast messages provide a non-intrusive way to inform users of important events, such as successful actions or errors, without disrupting their workflow. They can be easily dismissed and do not require page reloads or navigations. If there is no actionable item for the user to take, avoid using modal popups and favor toast messages instead.

## C# Best Practices

1. Keep models simple and focused, and in a Models folder.
2. Use data annotations for model validation.
3. Use async/await for all I/O-bound operations.
4. Follow SOLID principles to ensure maintainable and scalable code.
5. Use dependency injection to manage service lifetimes and dependencies.

### Coding Stanards

1. Keep components small and focused. Refactor to smaller components where it makes sense.
2. Reuse common UI components already built. Do not use markup like `<div class="alert">` when an <Alert> component exists.
3. Use try/catch blocks and a loading state boolean to ensure UI responsiveness.

### Database

1. The default database setup is SQLite but allow a real relational database.
2. Use EF Core to provide support for multiple databases.
3. Use LINQ queries efficiently.

## Jobs and Providers

The application relies on various AI providers to perform coding tasks. Each provider has its own set of capabilities, limitations, and cost structures. VibeSwarm manages the selection and utilization of these providers based on project requirements and agent configurations. Jobs are tasks that contain enough context for an AI agent to operate on. Jobs are created based on user goals and project needs, and are assigned to appropriate agents for execution.

## Dashboard

The VibeSwarm dashboard provides a comprehensive overview of what is going on in the application. Users are interested in status of jobs, token costs, usage limits, and alerts to actionable items.

## Git Integration

The application has a deep reliance on Git for version control of software, but is not required. Users must be able to queue up code changes suggested by agents and review them before applying to the codebase. Git is the preferred method for managing code changes, allowing for easy tracking of modifications and collaboration among multiple agents.

## Tool Use and Research

When working on this application, use best practices for prompt engineering and LLM interactions. Always attempt to research the latest techniques and strategies for working with AI coding agents. This includes staying informed about new models, providers, and tools that can enhance the application's capabilities.

## Goal Prompts

Users may enter small, vague prompts that might need expansion to accurately capture the intended goal. These are critical to the success of the application. Attempt to inject the best practices of an LLM to turn ideas into well formed goals. A goal should always have an overview, multiple objectives, and a clear end goal.

## CLI Agent Process Lifecycle Management

VibeSwarm deeply integrates with various CLI-based AI coding agents. It manages the lifecycle of these agent processes, ensuring they operate efficiently and effectively. VibeSwarm spawns agent processes as needed, monitors their performance, and handles communication between the agents and the main application.

## The Goal of VibeSwarm

VibeSwarm is an agentic CI/CD system for turning ideas into application code. The user brings their own CLI agent tools, and Git connections. VibeSwarm does not want to handle user sensitive data such as API keys.

## Application Security

Always ensure security best practices are followed. Never hard-code a key. Always rely on external sources such as environment variables. Any keys should have a single place they are mapped from environment variables to application configuration.

Ensure all user inputs are validated and sanitized to prevent injection attacks and other security vulnerabilities. Follow the principle of least privilege when managing access to resources and data within the application.

## Prompt Generation

When generating a prompt, use an XML-style approach to give the best segmentation of context. A task to complete might include an <overview>, multiple <objective> tags, and a <goal> tag.

# Critical Goals

1. Avoid custom CSS wherever possible. Use Bootstrap utility classes to achieve desired layouts and styling. If you have to make a class, name it as a utility and reuse it wherever possible.
2. Rely on frameworks and existing features. Less code in the project is better.
3. Split concerns when logically appropriate. We don't need massive files that do everything. We also don't need 100 files that do one thing each. Never make a class that only inherits another and nothing else.
4. Keep the user in control. The user should always be able to review and approve changes before they are applied.
5. Ensure the application is secure and follows best practices in coding and data protection.
6. Make the application easy to set up and use, even on low-powered devices.
7. Strive for cross-platform compatibility, ensuring the application runs smoothly on Windows, macOS, and Linux.
8. Don't get hung up on Docker right now - the user provides their environment with CLI tools that change rapidly.
