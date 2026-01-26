# VibeSwarm

VibeSwarm is an AI coding agent orchestrator that leverages multiple AI models to assist developers in writing, reviewing, and optimizing code. It integrates with various AI services to provide a seamless coding experience. VibeSwarm is intended to be hosted on a VPS, local machine, Raspberry Pi, or cloud instance. VibeSwarm exposes a web interface for managing multiple projects that are able to be autonomously coded by a swarm of AI agents.

## Requirements

VibeSwarm expects a host system with coding agents installed and signed in. As AI coding agents are rapidly changing, VibeSwarm attempts to be flexible and work with a variety of agents and CLI tools. VibeSwarm is the top level management interface for various CLI tools and implementing loops of agents to achieve coding goals.

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

## VibeSwarm Architecture

VibeSwarm is intended to stay simple but modular in 3 parts, the Web UI, the Worker service that manages agents, and a Shared library for common code both services use. Code relevant to UI should go in the Web project, code relevant to agent management should go in the Worker project, and any code shared between the two should go in the Shared project.

Utility classes should exist in the Shared project to be used by both the Web and Worker projects. Data models should also exist in the Shared project to ensure consistency between the Web and Worker services.

## Usage Limits for AI Agents

Some providers and models have usage limits, such as the number of requests per minute or total tokens per month. VibeSwarm monitors these limits and manages agent activity to avoid exceeding them. If an agent approaches its limit, VibeSwarm can throttle its requests or temporarily disable it until the limit resets.

VibeSwarm can utilize local AI models to supplement cloud-based agents, ensuring continuous operation even when usage limits are reached. This hybrid approach allows for greater flexibility and reliability in code generation and review tasks.

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

## Bootstrap Integration

Always attempt to leverage Bootstrap's built-in classes and components before creating custom styles. This ensures consistency across the application and reduces the need for additional CSS. Use a stacked utility class approach when creating UI components to maximize flexibility and maintainability. A component may use multiple classes such as `d-flex`, `flex-column`, `align-items-center`, and `p-3` to achieve the desired layout and styling without custom CSS. Use the TailwindCSS mindset when applying Bootstrap utility classes to create complex layouts and designs.

## Bootstrap Icons

VibeSwarm uses Bootstrap Icons for visual enhancements and to improve user experience. Icons are used throughout the interface to represent actions, statuses, and navigation elements. The application leverages the extensive library of Bootstrap Icons to maintain a consistent look and feel.

When adding icons, prefer using Bootstrap Icons over custom SVGs or other icon libraries to ensure visual consistency. Icons should be appropriately sized and aligned within UI components to enhance usability without overwhelming the design.

## Blazor Integration

VibeSwarm is built using Blazor, a web framework for building interactive web applications with C# and .NET. The application leverages Blazor's component-based architecture to create reusable UI components and manage application state effectively. Blazor's capabilities allow for seamless integration with backend services, enabling real-time updates and dynamic content rendering. The SignalR library is used to facilitate real-time communication between the server and client, allowing for instant updates on agent activities and project status.

## UI Components

Large pages should be broken into smaller, reusable components to improve maintainability and readability. Components such as agent cards, project lists, and status indicators can be created to encapsulate specific functionality and styling. This modular approach allows for easier updates and enhancements to individual components without affecting the overall application.

UI should appear consistent and highly polished. Care should be used to maintain alignment, spacing, and visual hierarchy throughout the application. Attention to detail in UI design enhances user experience and promotes a professional appearance. The application must also be responsive and mobile-friendly, ensuring usability across a range of devices.

## Database

VibeSwarm uses a relational database to store project configurations, agent settings, and code history. The database schema is designed to efficiently manage relationships between projects, agents, and code changes. The application uses an ORM (Object-Relational Mapping) tool to interact with the database, allowing for easier data manipulation and retrieval.

The database is the source of truth for coordination across multiple agents. It ensures that all agents have access to the latest project information and code history, enabling them to operate effectively and collaboratively.
The database should be designed to handle concurrent access from multiple agents, ensuring data integrity and consistency. Proper indexing and optimization techniques should be employed to maintain performance as the number of projects and agents grows.
The database should support backup and recovery mechanisms to protect against data loss. Regular backups should be scheduled, and recovery procedures should be in place to restore data in case of failures.

## Dashboard

The VibeSwarm dashboard provides a comprehensive overview of all projects and agents. It displays the status of each agent, recent code changes, and overall project health. Users can monitor agent activities, view logs, and manage project settings from the dashboard.
The dashboard should provide real-time updates on agent activities, allowing users to see the progress of code generation and review tasks as they happen. Notifications and alerts can be used to inform users of important events, such as agent errors or completed tasks.

## Git Integration

VibeSwarm integrates with Git to manage code versioning and collaboration. Each project is generally associated with a Git repository, allowing agents to pull the latest code, make changes, and push updates back to the repository. The application uses Git commands to handle branching, merging, and conflict resolution as needed. If a Git repository is not available, VibeSwarm can operate on a local code directory, but Git integration is preferred for version control and collaboration.

VibeSwarm assumes git is installed on the host system and accessible via the command line. The application uses Git to track code changes made by agents, providing a history of modifications and enabling easy rollback if necessary. Even if a project is not initially set up as a Git repository, VibeSwarm can initialize a new repository in the project directory to enable version control.

VibeSwarm assumes the login and credentials for any remote Git repositories are already set up on the host system. This may include SSH keys, access tokens, or other authentication methods required to interact with private repositories. VibeSwarm does not handle Git authentication directly but relies on the host system's configuration.

## Open Source

VibeSwarm is for everyone looking to build at light speed. Although VibeSwarm is open source, its maintainers retain the right to deny contributions or usage that conflict with the project's goals or values. Users and contributors must adhere to the project's code of conduct and contribution guidelines to ensure a positive and collaborative environment.
