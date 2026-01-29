# VibeSwarm

VibeSwarm is an AI coding agent orchestrator that leverages multiple AI models to assist developers in writing, reviewing, and optimizing code. It integrates with various AI services to provide a seamless coding experience. VibeSwarm is intended to be hosted on a VPS, local machine, Raspberry Pi, or cloud instance. VibeSwarm exposes a web interface for managing multiple projects that are able to be autonomously coded by a swarm of AI agents.

## Requirements

A user of VibeSwarm is expected to have accounts with various AI providers and have the necessary API keys or authentication tokens available for use by VibeSwarm. The user is also expected to have a basic understanding of how to set up and configure AI coding agents on their host system.

## Features

Projects are set up in VibeSwarm which define a code directory and a set of AI agents to operate on that code. Each agent has a specific role, such as code generation, code review, or optimization.

Agents can communicate with each other to collaborate on tasks, share insights, and improve code quality. Any collaboration between agents is managed by VibeSwarm, which coordinates their activities and ensures that they work together effectively using the project database as the source of truth.

Progress is tracked through a dashboard that shows the status of each agent, recent changes, and overall project health. The application database stores project configurations, agent settings, and code history to allow for easy retrieval and management among multiple agents.

## Agent Management

VibeSwarm attempts to use various CLI agent providers together. The primary providers are:

- GitHub Copilot CLI
- Claude Code
- OpenCode

Once providers tools are configured, VibeSwarm can spawn processes for each agent defined in a project. Each agent runs its assigned tasks and communicates results back to the main application. VibeSwarm manages the lifecycle of these agent processes, ensuring they operate efficiently and effectively.

VibeSwarm will monitor agent performance and resource usage, allowing for dynamic adjustments to agent activity based on project needs. This includes starting, stopping, or pausing agents as necessary to optimize overall performance. Token usage and costs associated with each agent are tracked to help manage expenses and ensure efficient use of AI resources.

VibeSwarm will attempt to get the job done by intelligently switching models and providers if one is not performing adequately. This ensures that projects continue to progress even if certain agents encounter issues or limitations. VibeSwarm will also not attempt to utilize all tokens or usage at once, but will stagger agent activity to maintain a steady workflow and avoid overwhelming the system. VibeSwarm will wait between agent interactions to allow for processing time and to prevent rate limiting by AI providers.

VibeSwarm assumes authentication and configuration for each agent provider is already set up on the host system. This includes any necessary API keys, tokens, or login credentials required for the agents to operate. VibeSwarm does not handle authentication directly but relies on the host system's configuration.

## Cross Platform

VibeSwarm is designed to be cross-platform, running on Windows, macOS, and Linux. It uses platform-agnostic libraries and tools to ensure compatibility across different operating systems. The application detects the operating system at runtime and adjusts its behavior accordingly to provide a consistent user experience.

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

## Cost Management

VibeSwarm tracks the costs associated with each AI agent and provider. It provides insights into usage patterns and expenses, allowing users to make informed decisions about which agents to use. VibeSwarm can also implement cost-saving strategies, such as switching to lower-cost models or providers when appropriate.

VibeSwarm allows users to set budget limits for AI agent usage. If a budget limit is reached, VibeSwarm can pause agent activity or switch to more cost-effective alternatives to prevent overspending.

## Fresh Context Windows

VibeSwarm ensures that AI agents operate with fresh context windows by periodically resetting their context or providing them with updated information from the codebase. This helps maintain relevance and accuracy in code suggestions and reviews.

VibeSwarmed is designed to utilize loops of agents where one agent's output can be used as another agent's input. This allows for iterative improvements and refinements in code generation and review processes.

## Reinforcement Learning

VibeSwarm incorporates reinforcement learning techniques to improve agent performance over time. Agents learn from their interactions with the codebase and user feedback, adapting their strategies to produce better results. By analyzing successful code changes and user preferences, agents can refine their approaches to code generation and review, leading to more efficient and effective coding assistance.

## Agent Skills

VibeSwarm attempts to generate agent skills based on the project type and programming languages used. For example, a web development project may have agents skilled in HTML, CSS, JavaScript, and popular frameworks like React or Angular. A data science project may have agents proficient in Python, R, and relevant libraries.

These skills enable agents to provide targeted assistance, ensuring that code suggestions and reviews are relevant to the specific technologies and practices used in the project.

The goal of VibeSwarm is to get better over time at identifying the necessary skills for each project and configuring agents accordingly. This adaptive approach helps ensure that agents remain effective as project requirements evolve.

## User Interface

This application is currently built using the Boostrap v5.x framework for styling and layout. The user interface is designed to be intuitive and user-friendly, allowing developers to easily navigate through projects, manage agents, and monitor progress. The dashboard provides a clear overview of agent activities, recent changes, and project status, making it easy for users to stay informed and in control of their coding assistance.

Attempt to utilize existing Bootstrap components and utility classes to maintain consistency and reduce custom styling. Future enhancements may include additional UI frameworks or libraries to further improve the user experience.

The interface must be fully responsive and work on mobile devices as well as desktops. The layout should adapt to different screen sizes, ensuring usability across a range of devices. The application should follow accessibility best practices to ensure that all users, including those with disabilities, can effectively use the interface. This includes proper use of ARIA roles, keyboard navigation support, and sufficient color contrast.

## Mobile Friendly

VibeSwarm is designed to be mobile-friendly, ensuring that users can access and manage their projects from smartphones and tablets. The responsive design adapts to various screen sizes, providing an optimal user experience regardless of the device being used. The mobile interface includes touch-friendly elements and simplified navigation to facilitate ease of use on smaller screens.

All pages and UI components are tested on an iPhone to ensure every aspect of the application works from anywhere the application is accessible. A best attempt is made to ensure the viewport is optimized for mobile devices and desktop devices alike with proper spacing, padding, and alignment.

## Desktop Friendly

The UI should also be optimized for desktop use, taking advantage of larger screen real estate to provide a more detailed and comprehensive view of projects and agent activities. The desktop interface includes additional features and information that may not be necessary on mobile devices, enhancing the overall user experience for desktop users.

Consider the entire viewport when designing layouts, ensuring that content is well-organized and easily accessible on larger screens. Utilize grid systems and flexible layouts to create a visually appealing and functional desktop interface. Ensure there are no gaps of unused space on larger screens by expanding content areas or adding supplementary information where appropriate.

Elements such as Modals should expand to a reasonable size on desktop screens to improve usability, while still being fully functional on mobile devices.

## Bootstrap Integration

Always attempt to leverage Bootstrap's built-in classes and components. Avoid custom styles at all costs, unless absolutely necessary. Custom classes should ONLY exist when there is no equivalent in Bootstrap and should be named using a clear and consistent naming convention. This ensures consistency across the application and reduces the need for additional CSS. Use a stacked utility class approach when creating UI components to maximize flexibility and maintainability. A component may use multiple classes such as `d-flex`, `flex-column`, `align-items-center`, `bg-body-secondary`, and `p-3` to achieve the desired layout and styling without custom CSS. Use the TailwindCSS mindset when applying Bootstrap utility classes to create complex layouts and designs.

## Style.css

The application specific `style.css` should only add helper utilities that can be used across the application, and are not intended to be specific to components.

No components should need a `*.razor.css` file. All styling should be achievable via Bootstrap utility classes alone. If a component requires specific styling that cannot be achieved with Bootstrap classes, consider refactoring the component to better align with Bootstrap's capabilities.

The `style.css` should never contain specific component styles. It should only contain helper classes that can be used across multiple components. Razor components should stack Bootstrap utility classes to achieve the desired styling and layout.

## Bootstrap Icons

VibeSwarm uses Bootstrap Icons for visual enhancements and to improve user experience. Icons are used throughout the interface to represent actions, statuses, and navigation elements. The application leverages the extensive library of Bootstrap Icons to maintain a consistent look and feel.

When adding icons, prefer using Bootstrap Icons over custom SVGs or other icon libraries to ensure visual consistency. Icons should be appropriately sized and aligned within UI components to enhance usability without overwhelming the design.

Icon usage in buttons should always contain a Title attribute for accessibility and better user experience. This provides additional context for users, especially those using screen readers.

## Blazor Integration

VibeSwarm is built using Blazor, a web framework for building interactive web applications with C# and .NET. The application leverages Blazor's component-based architecture to create reusable UI components and manage application state effectively. Blazor's capabilities allow for seamless integration with backend services, enabling real-time updates and dynamic content rendering. The SignalR library is used to facilitate real-time communication between the server and client, allowing for instant updates on agent activities and project status.

Data fetching methods should use try/catch blocks to handle potential errors gracefully. This ensures that the application remains stable and provides informative feedback to users in case of data retrieval issues. Loading indicators should be displayed while data is being fetched to enhance user experience and provide visual feedback during asynchronous operations. The UI should not be blocked while waiting for data to load.

## UI Components

Large pages should be broken into smaller, reusable components to improve maintainability and readability. Components such as agent cards, project lists, and status indicators can be created to encapsulate specific functionality and styling. This modular approach allows for easier updates and enhancements to individual components without affecting the overall application.

UI should appear consistent and highly polished. Care should be used to maintain alignment, spacing, and visual hierarchy throughout the application. Attention to detail in UI design enhances user experience and promotes a professional appearance. The application must also be responsive and mobile-friendly, ensuring usability across a range of devices.

If a page has over 300 lines of markup, it should be refactored into smaller components to keep the markup readable.

Avoid table based layouts for non-tabular data. Use Bootstrap's grid system and flexbox utilities to create responsive and flexible layouts that adapt to different screen sizes. Utilize lists with list group items, cards, and other Bootstrap components to structure content effectively without relying on tables. Tables are prone to responsiveness issues and should be reserved for displaying tabular data only.

## C# Best Practices

Use a Models folder to contain all data models used in the application. This promotes organization and makes it easier to locate and manage data structures. Use attributes such as [Required], [StringLength], and [Range] to enforce data validation rules on models. This ensures that data integrity is maintained and reduces the likelihood of errors during data processing.

When working with asynchronous operations, prefer using async/await patterns to improve application responsiveness and scalability. This allows for non-blocking operations, enhancing user experience during long-running tasks.

Use dependency injection to manage service lifetimes and dependencies. This promotes loose coupling and enhances testability by allowing for easier mocking of services during unit testing.

## Database

VibeSwarm uses a relational database to store project configurations, agent settings, and code history. The database schema is designed to efficiently manage relationships between projects, agents, and code changes. The application uses an ORM (Object-Relational Mapping) tool to interact with the database, allowing for easier data manipulation and retrieval.

The database is the source of truth for coordination across multiple agents. It ensures that all agents have access to the latest project information and code history, enabling them to operate effectively and collaboratively.

The database should be designed to handle concurrent access from multiple agents, ensuring data integrity and consistency. Proper indexing and optimization techniques should be employed to maintain performance as the number of projects and agents grows.

The database should support backup and recovery mechanisms to protect against data loss. Regular backups should be scheduled, and recovery procedures should be in place to restore data in case of failures.

The database should be referred to for any locking procedures to ensure if multiple instances of VibeSwarm are used together there are no concurrency issues.

## Dashboard

The VibeSwarm dashboard provides a comprehensive overview of all projects and agents. It displays the status of each agent, recent code changes, and overall project health. Users can monitor agent activities, view logs, and manage project settings from the dashboard.

The dashboard should provide real-time updates on agent activities, allowing users to see the progress of code generation and review tasks as they happen. Notifications and alerts can be used to inform users of important events, such as agent errors or completed tasks.

## Git Integration

VibeSwarm integrates with Git to manage code versioning and collaboration. Each project is generally associated with a Git repository, allowing agents to pull the latest code, make changes, and push updates back to the repository. The application uses Git commands to handle branching, merging, and conflict resolution as needed. If a Git repository is not available, VibeSwarm can operate on a local code directory, but Git integration is preferred for version control and collaboration.

VibeSwarm assumes git is installed on the host system and accessible via the command line. The application uses Git to track code changes made by agents, providing a history of modifications and enabling easy rollback if necessary. Even if a project is not initially set up as a Git repository, VibeSwarm can initialize a new repository in the project directory to enable version control.

VibeSwarm assumes the login and credentials for any remote Git repositories are already set up on the host system. This may include SSH keys, access tokens, or other authentication methods required to interact with private repositories. VibeSwarm does not handle Git authentication directly but relies on the host system's configuration.
