# VibeSwarm

VibeSwarm is a web hostable wrapper for managing multiple CLI agents (Providers) and source code repositories (Projects). The app provides a consistent interface across different agents to allow running code improvements in the background and managing from a mobile interface.

## Migrations

Use dotnet ef migrations to manage database schema changes. When adding new features that require database changes, create a new migration. Always attempt a `dotnet build` before creating a migration to ensure there are no build errors. After creating a migration, run `dotnet ef database update` to apply the changes to the database.

ALWAYS make sure migrations are generated correctly and tested before committing. If you encounter issues with migrations, you can use `dotnet ef migrations remove` to delete the last migration and try again.

## Building

ALWAYS run `dotnet build` before running the application to ensure there are no build errors. If you encounter build errors, address them before proceeding. This will help maintain a stable codebase and prevent issues when running the application.

ALWAYS run `dotnet test` after making code changes and fixing any build errors. All tests must pass before considering the work complete. If tests fail, fix them before finishing.

## Build Verification

VibeSwarm enforces build verification to prevent broken code from being committed or pushed by CLI agents. This is a critical safety mechanism.

### How It Works

1. **Agent-side**: The system prompt injected into every agent session includes explicit instructions to verify the build and run tests before finishing. If a project has `BuildCommand` or `TestCommand` configured, those exact commands are included in the agent instructions.

2. **Server-side**: After a job completes successfully, VibeSwarm runs the project's configured build and test commands before auto-committing or pushing changes. If the build or tests fail:
   - Changes are NOT auto-committed or pushed
   - The job is marked with `BuildVerified = false`
   - Build output is captured in `BuildOutput` for debugging
   - Changes remain in the working directory for manual review

### Project Configuration

Each project can configure:
- `BuildVerificationEnabled` (bool) - Whether to run verification after jobs
- `BuildCommand` (string) - Shell command to verify the build (e.g., `dotnet build`, `npm run build`)
- `TestCommand` (string) - Shell command to verify tests pass (e.g., `dotnet test`, `npm test`)

### For This Repository

When working on VibeSwarm itself, always:
1. Run `dotnet build` to verify compilation
2. Run `dotnet test` to verify all tests pass
3. Fix any failures before committing

## Providers

Users install providers on the same host as VibeSwarm. Each provider is a CLI agent that can be run in the background and managed through the VibeSwarm interface. Providers can be configured to run specific tasks, such as code analysis, refactoring, or testing.

### Claude Code

Changelog URL: https://raw.githubusercontent.com/anthropics/claude-code/refs/heads/main/CHANGELOG.md

### GitHub Copilot

Changelog URL: https://raw.githubusercontent.com/github/copilot-cli/refs/heads/main/changelog.md

### OpenCode

Changelog URL: https://opencode.ai/changelog
