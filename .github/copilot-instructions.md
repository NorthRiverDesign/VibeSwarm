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

VibeSwarm is designed to be cross-platform, running on Windows, macOS, and Linux. It uses platform-agnostic libraries and tools to ensure compatibility across different operating systems. The application detects the operating system at runtime and adjusts its behavior accordingly to provide a consistent user experience.

Because VibeSwarm relies on CLI-based AI agents, it is important that the host system has the necessary tools and dependencies installed for each agent to function properly. VibeSwarm provides documentation and guidance on setting up these agents on various platforms to ensure smooth operation.

## Deployment

This app is intended to work on a variety of host systems, including local machines, VPS, Raspberry Pi, and cloud instances. The app attempts to remain as system agnostic as possible, relying on widely supported technologies and frameworks. Because VibeSwarm relies on system binaries, containerization is not currently supported.

A future update may include Docker support to simplify deployment and ensure consistent environments across different host systems. This would involve creating Docker images that package the application along with its dependencies, allowing for easy deployment on any system that supports Docker.

## VibeSwarm Architecture

VibeSwarm is intended to stay simple but modular in 3 parts, the Web UI, the Worker service that manages agents, and a Shared library for common code both services use. Code relevant to UI should go in the Web project, code relevant to agent management should go in the Worker project, and any code shared between the two should go in the Shared project.

Utility classes should exist in the Shared project to be used by both the Web and Worker projects. Data models should also exist in the Shared project to ensure consistency between the Web and Worker services.

## Coding Best Practices

VibeSwarm follows coding best practices to ensure maintainability, readability, and performance. This includes adhering to established coding standards, using meaningful variable and function names, and writing modular code. The application is structured to promote separation of concerns, with distinct layers for data access, business logic, and presentation.

Because VibeSwarm interacts with rapidly changing AI agents and services, the codebase is designed to be flexible and adaptable. This includes using interfaces and abstractions to allow for easy integration of new agents and providers as they become available. Service provider implementations should be encapsulated to minimize the impact of changes on the overall application.

The C# coding style follows the official Microsoft C# coding conventions. Consistent formatting, indentation, and spacing are used throughout the codebase to enhance readability. Comments and documentation are provided where necessary to explain complex logic and provide context for future developers.

## Usage Limits for AI Agents

Some providers and models have usage limits, such as the number of requests per minute or total tokens per month. VibeSwarm monitors these limits and manages agent activity to avoid exceeding them. If an agent approaches its limit, VibeSwarm can throttle its requests or temporarily disable it until the limit resets.

VibeSwarm can utilize local AI models to supplement cloud-based agents, ensuring continuous operation even when usage limits are reached. This hybrid approach allows for greater flexibility and reliability in code generation and review tasks.

VibeSwarm will not overly spam the provider and must wait for a short duration between executions per provider in order to avoid overloading the underlying service provider.

## Cost Management

The user may or may not be billed based on provider usage. All attempts must be made to relay usage statistics back to the application and in a format that allows stop signals and/or circuit breakers. The user must remain fully in control of what limits they allow VibeSwarm to delegate to CLI agents, even if there is work to do in the application.

## Fresh Context Windows

VibeSwarm always starts jobs with a fresh context window and/or session on the CLI provider. The session ID through the provider (if available) is stored to facilitate resuming operations in the event of an error. Always attempt to give Jobs the best possible chance of succeeding according to user intent.

## Agent Skills

VibeSwarm has a Skills feature where instructions may be present for specific tasks across various projects. Always attempt to expose these to a CLI agent in order to get the best possible outcome.

## User Interface

This application is currently built using the Boostrap v5.x framework for styling and layout. The user interface is designed to be intuitive and user-friendly, allowing developers to easily navigate through projects, manage agents, and monitor progress. The dashboard provides a clear overview of agent activities, recent changes, and project status, making it easy for users to stay informed and in control of their coding assistance.

Attempt to utilize existing Bootstrap components and utility classes to maintain consistency and reduce custom styling. Future enhancements may include additional UI frameworks or libraries to further improve the user experience.

The interface must be fully responsive and work on mobile devices as well as desktops. The layout should adapt to different screen sizes, ensuring usability across a range of devices. The application should follow accessibility best practices to ensure that all users, including those with disabilities, can effectively use the interface. This includes proper use of ARIA roles, keyboard navigation support, and sufficient color contrast.

Always ensure consistent spacing, alignment, and visual hierarchy throughout the application. Attention to detail in UI design enhances user experience and promotes a professional appearance. The viewport is considered when designing layouts, ensuring that content is well-organized and easily accessible on various screen sizes. There should never be instances of large gaps, or unused space on larger screens.

Classes should never target one single concern, such as `.goal-prompt-textarea` or `.agent-card`. Instead, use a stacked utility class approach to achieve the desired layout and styling without custom CSS. This ensures flexibility and maintainability across the application. A component may use multiple classes such as `d-flex`, `flex-column`, `align-items-center`, `bg-body-secondary`, and `p-3` to achieve the desired layout and styling without custom CSS.

Consider the entire viewport when designing layouts, ensuring that content is well-organized and easily accessible on different screen sizes. Utilize grid systems and flexible layouts to create a visually appealing and functional interface. When on a larger screen, ensure there are no gaps of unused space by expanding content areas or adding supplementary information where appropriate.

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

## Blazor Integration

VibeSwarm is built using Blazor, a web framework for building interactive web applications with C# and .NET. The application leverages Blazor's component-based architecture to create reusable UI components and manage application state effectively. Blazor's capabilities allow for seamless integration with backend services, enabling real-time updates and dynamic content rendering. The SignalR library is used to facilitate real-time communication between the server and client, allowing for instant updates on agent activities and project status.

Data fetching methods should use try/catch blocks to handle potential errors gracefully. This ensures that the application remains stable and provides informative feedback to users in case of data retrieval issues. Loading indicators should be displayed while data is being fetched to enhance user experience and provide visual feedback during asynchronous operations. The UI should not be blocked while waiting for data to load.

Do not use code-behind files for Razor components. All logic should be contained within the `.razor` file itself to maintain clarity and reduce complexity. This approach simplifies component management and enhances readability.

Attempt to keep Razor components under 300 lines of markup and code combined. If a component exceeds this limit, consider refactoring it into smaller, more manageable components to improve maintainability and readability.

All interaction should be real-time using SignalR where applicable. Avoid page reloads or full page navigations for data updates. Instead, use Blazor's data binding and event handling capabilities to provide a seamless and dynamic user experience.

The application will be primarily used as a PWA (Progressive Web App). Ensure that all Blazor components and pages are optimized for PWA usage, including offline capabilities, responsive design, and efficient resource loading. The application should not have any issues with loading or interactivity when used as a PWA, even if the user has not opened the app for an extended period.

### UI Components

Large pages should be broken into smaller, reusable components to improve maintainability and readability. Components such as agent cards, project lists, and status indicators can be created to encapsulate specific functionality and styling. This modular approach allows for easier updates and enhancements to individual components without affecting the overall application.

UI should appear consistent and highly polished. Care should be used to maintain alignment, spacing, and visual hierarchy throughout the application. Attention to detail in UI design enhances user experience and promotes a professional appearance. The application must also be responsive and mobile-friendly, ensuring usability across a range of devices.

If a page has over 300 lines of markup, it should be refactored into smaller components to keep the markup readable.

Avoid table based layouts for non-tabular data. Use Bootstrap's grid system and flexbox utilities to create responsive and flexible layouts that adapt to different screen sizes. Utilize lists with list group items, cards, and other Bootstrap components to structure content effectively without relying on tables. Tables are prone to responsiveness issues and should be reserved for displaying tabular data only.

### Toast Messages

Favor using toast messages instead of Flash messages for user notifications. Toast messages provide a non-intrusive way to inform users of important events, such as successful actions or errors, without disrupting their workflow. They can be easily dismissed and do not require page reloads or navigations. If there is no actionable item for the user to take, avoid using modal popups and favor toast messages instead.

## C# Best Practices

Use a Models folder to contain all data models used in the application. This promotes organization and makes it easier to locate and manage data structures. Use attributes such as [Required], [StringLength], and [Range] to enforce data validation rules on models. This ensures that data integrity is maintained and reduces the likelihood of errors during data processing.

Classes should follow the Single Responsibility Principle, ensuring that each class has a clear and focused purpose. This enhances maintainability and makes it easier to understand the codebase. Class files should be named according to the class they contain, following PascalCase naming conventions. This promotes consistency and makes it easier to locate specific classes within the project. Keep class files in a one class per file structure to enhance readability and maintainability.

When working with asynchronous operations, prefer using async/await patterns to improve application responsiveness and scalability. This allows for non-blocking operations, enhancing user experience during long-running tasks.

Use dependency injection to manage service lifetimes and dependencies. This promotes loose coupling and enhances testability by allowing for easier mocking of services during unit testing.

### Coding Stanards

The less code the better. Always attempt to write the simplest code possible to achieve the desired functionality. Avoid unnecessary complexity and strive for clarity in code structure and logic. Rely on framework features and libraries to reduce boilerplate code and improve maintainability.

No individual file should exceed 500 lines of code. If a file exceeds this limit, consider refactoring it into smaller, more manageable files to improve readability and maintainability. Use good OOP principles such as encapsulation, inheritance, and polymorphism to create a well-structured and modular codebase. This promotes code reuse and enhances maintainability.

### Database

The relational database (default SQLite) is the source of truth for all application data. Use Entity Framework Core as the ORM to manage database interactions. Define DbContext classes to represent the database context and DbSet properties for each entity model. Use migrations to manage database schema changes and ensure consistency across different environments.

The application should query the database efficiently, using LINQ queries and eager loading where appropriate to minimize the number of database calls. Avoid loading unnecessary data and use pagination for large datasets to improve performance.

Multiple instances of the application should be able to connect to the same database without causing data corruption or conflicts. Use proper transaction management and concurrency control mechanisms to ensure data integrity when multiple instances are accessing and modifying the database simultaneously.

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
