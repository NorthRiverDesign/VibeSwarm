# VibeSwarm

VibeSwarm is a web hostable wrapper for managing multiple CLI agents (Providers) and source code repositories (Projects). The app provides a consistent interface across different agents to allow running code improvements in the background and managing from a mobile interface.

## Migrations

Use dotnet ef migrations to manage database schema changes. When adding new features that require database changes, create a new migration. Always attempt a `dotnet build` before creating a migration to ensure there are no build errors. After creating a migration, run `dotnet ef database update` to apply the changes to the database.

ALWAYS make sure migrations are generated correctly and tested before committing. If you encounter issues with migrations, you can use `dotnet ef migrations remove` to delete the last migration and try again.

## Building

ALWAYS run `dotnet build` before running the application to ensure there are no build errors. If you encounter build errors, address them before proceeding. This will help maintain a stable codebase and prevent issues when running the application.

## Providers

Users install providers on the same host as VibeSwarm. Each provider is a CLI agent that can be run in the background and managed through the VibeSwarm interface. Providers can be configured to run specific tasks, such as code analysis, refactoring, or testing.

### Claude Code

Changelog URL: https://raw.githubusercontent.com/anthropics/claude-code/refs/heads/main/CHANGELOG.md

### GitHub Copilot

Changelog URL: https://raw.githubusercontent.com/github/copilot-cli/refs/heads/main/changelog.md

### OpenCode

Changelog URL: https://opencode.ai/changelog
