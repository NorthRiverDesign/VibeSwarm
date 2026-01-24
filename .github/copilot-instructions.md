# VibeSwarm

VibeSwarm is an AI coding agent orchestrator that leverages multiple AI models to assist developers in writing, reviewing, and optimizing code. It integrates with various AI services to provide a seamless coding experience.

## Features

Projects are set up in VibeSwarm which define a code directory and a set of AI agents to operate on that code. Each agent has a specific role, such as code generation, code review, or optimization.

Agents can communicate with each other to collaborate on tasks, share insights, and improve code quality.

Progress is tracked through a dashboard that shows the status of each agent, recent changes, and overall project health. The application database stores project configurations, agent settings, and code history to allow for easy retrieval and management among multiple agents.

## Agent Management

VibeSwarm attempts to use various CLI agent providers together. The primary providers are:

- GitHub Copilot CLI
- Claude Code
- OpenCode

Once providers tools are configured, VibeSwarm can spawn processes for each agent defined in a project. Each agent runs its assigned tasks and communicates results back to the main application. VibeSwarm manages the lifecycle of these agent processes, ensuring they operate efficiently and effectively.

## Cross Platform

VibeSwarm is designed to be cross-platform, running on Windows, macOS, and Linux. It uses platform-agnostic libraries and tools to ensure compatibility across different operating systems. The application detects the operating system at runtime and adjusts its behavior accordingly to provide a consistent user experience.

## Usage Limits for AI Agents

Some providers and models have usage limits, such as the number of requests per minute or total tokens per month. VibeSwarm monitors these limits and manages agent activity to avoid exceeding them. If an agent approaches its limit, VibeSwarm can throttle its requests or temporarily disable it until the limit resets.

VibeSwarm can utilize local AI models to supplement cloud-based agents, ensuring continuous operation even when usage limits are reached. This hybrid approach allows for greater flexibility and reliability in code generation and review tasks.

## Reinforcement Learning

VibeSwarm incorporates reinforcement learning techniques to improve agent performance over time. Agents learn from their interactions with the codebase and user feedback, adapting their strategies to produce better results. By analyzing successful code changes and user preferences, agents can refine their approaches to code generation and review, leading to more efficient and effective coding assistance.

## Agent Skills

VibeSwarm attempts to generate agent skills based on the project type and programming languages used. For example, a web development project may have agents skilled in HTML, CSS, JavaScript, and popular frameworks like React or Angular. A data science project may have agents proficient in Python, R, and relevant libraries.

These skills enable agents to provide targeted assistance, ensuring that code suggestions and reviews are relevant to the specific technologies and practices used in the project.

## User Interface

This application is currently built using the Boostrap v5.x framework for styling and layout. The user interface is designed to be intuitive and user-friendly, allowing developers to easily navigate through projects, manage agents, and monitor progress. The dashboard provides a clear overview of agent activities, recent changes, and project status, making it easy for users to stay informed and in control of their coding assistance.

Attempt to utilize existing Bootstrap components and utility classes to maintain consistency and reduce custom styling. Future enhancements may include additional UI frameworks or libraries to further improve the user experience.

The interface must be fully responsive and work on mobile devices as well as desktops. The layout should adapt to different screen sizes, ensuring usability across a range of devices. The application should follow accessibility best practices to ensure that all users, including those with disabilities, can effectively use the interface. This includes proper use of ARIA roles, keyboard navigation support, and sufficient color contrast.

## UI Components

Large pages should be broken into smaller, reusable components to improve maintainability and readability. Components such as agent cards, project lists, and status indicators can be created to encapsulate specific functionality and styling. This modular approach allows for easier updates and enhancements to individual components without affecting the overall application.

## Database

VibeSwarm uses a relational database to store project configurations, agent settings, and code history. The database schema is designed to efficiently manage relationships between projects, agents, and code changes. The application uses an ORM (Object-Relational Mapping) tool to interact with the database, allowing for easier data manipulation and retrieval.

The database is the source of truth for coordination across multiple agents. It ensures that all agents have access to the latest project information and code history, enabling them to operate effectively and collaboratively.
The database should be designed to handle concurrent access from multiple agents, ensuring data integrity and consistency. Proper indexing and optimization techniques should be employed to maintain performance as the number of projects and agents grows.
The database should support backup and recovery mechanisms to protect against data loss. Regular backups should be scheduled, and recovery procedures should be in place to restore data in case of failures.

## Dashboard

The VibeSwarm dashboard provides a comprehensive overview of all projects and agents. It displays the status of each agent, recent code changes, and overall project health. Users can monitor agent activities, view logs, and manage project settings from the dashboard.
The dashboard should provide real-time updates on agent activities, allowing users to see the progress of code generation and review tasks as they happen. Notifications and alerts can be used to inform users of important events, such as agent errors or completed tasks.
