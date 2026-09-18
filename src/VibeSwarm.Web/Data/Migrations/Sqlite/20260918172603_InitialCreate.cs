using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeSwarm.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DefaultProjectsDirectory = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    TimeZoneId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, defaultValue: "UTC"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EnablePromptStructuring = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    InjectRepoMap = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    InjectEfficiencyRules = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    EnableCommitAttribution = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    CriticalErrorLogRetentionDays = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 30),
                    CriticalErrorLogMaxEntries = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 200),
                    IdeaExpansionPromptTemplate = table.Column<string>(type: "TEXT", maxLength: 12000, nullable: true),
                    IdeaImplementationPromptTemplate = table.Column<string>(type: "TEXT", maxLength: 12000, nullable: true),
                    ApprovedIdeaImplementationPromptTemplate = table.Column<string>(type: "TEXT", maxLength: 12000, nullable: true),
                    GitHubToken = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastLoginAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    ThemePreference = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false, defaultValue: "System"),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: true),
                    SecurityStamp = table.Column<string>(type: "TEXT", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "TEXT", nullable: true),
                    PhoneNumber = table.Column<string>(type: "TEXT", nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CriticalErrorLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Severity = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Details = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    TraceId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Url = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    UserAgent = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    RefreshAction = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    TriggeredRefresh = table.Column<bool>(type: "INTEGER", nullable: false),
                    AdditionalDataJson = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CriticalErrorLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InferenceProviders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ProviderType = table.Column<string>(type: "TEXT", nullable: false),
                    Endpoint = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ApiKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InferenceProviders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Projects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    WorkingPath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    GitHubRepository = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    AutoCommitMode = table.Column<int>(type: "INTEGER", nullable: false),
                    GitChangeDeliveryMode = table.Column<int>(type: "INTEGER", nullable: false),
                    DefaultTargetBranch = table.Column<string>(type: "TEXT", maxLength: 250, nullable: true),
                    PlanningEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    PlanningProviderId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PlanningModelId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    PlanningReasoningEffort = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    IdeaInferenceProviderId = table.Column<Guid>(type: "TEXT", nullable: true),
                    IdeaInferenceModelId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CommitSummaryInferenceProviderId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CommitSummaryInferenceModelId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PromptContext = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    Memory = table.Column<string>(type: "TEXT", maxLength: 20000, nullable: true),
                    RepoMap = table.Column<string>(type: "TEXT", nullable: true),
                    RepoMapGeneratedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    IdeasProcessingActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    IdeasAutoCommit = table.Column<bool>(type: "INTEGER", nullable: false),
                    IdeasProcessingProviderId = table.Column<Guid>(type: "TEXT", nullable: true),
                    IdeasProcessingModelId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    EnableTeamSwarm = table.Column<bool>(type: "INTEGER", nullable: false),
                    BuildVerificationEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    BuildCommand = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    TestCommand = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Providers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    ConnectionMode = table.Column<string>(type: "TEXT", nullable: false),
                    ExecutablePath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    WorkingDirectory = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ApiEndpoint = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ApiKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    MaxExecutionMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastConnectedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastModelsRefreshAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ConfiguredUsageLimit = table.Column<int>(type: "INTEGER", nullable: true),
                    ConfiguredLimitType = table.Column<string>(type: "TEXT", nullable: false),
                    StallTimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: true),
                    DefaultReasoningEffort = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Providers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Skills",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SourceType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SourceUri = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    SourceRef = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    StoragePath = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    AllowedTools = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    HasScripts = table.Column<bool>(type: "INTEGER", nullable: false),
                    InstalledAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Skills", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoleClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RoleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClaimType = table.Column<string>(type: "TEXT", nullable: true),
                    ClaimValue = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoleClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetRoleClaims_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClaimType = table.Column<string>(type: "TEXT", nullable: true),
                    ClaimValue = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetUserClaims_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserLogins",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderKey = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "TEXT", nullable: true),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_AspNetUserLogins_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserRoles",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RoleId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserTokens",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LoginProvider = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_AspNetUserTokens_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InferenceModels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InferenceProviderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ModelId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ParameterSize = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Family = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    QuantizationLevel = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    TaskType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsAvailable = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InferenceModels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InferenceModels_InferenceProviders_InferenceProviderId",
                        column: x => x.InferenceProviderId,
                        principalTable: "InferenceProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectEnvironments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Url = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    Stage = table.Column<string>(type: "TEXT", nullable: false),
                    IsPrimary = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    UsernameCiphertext = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    PasswordCiphertext = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectEnvironments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectEnvironments_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Agents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Responsibilities = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    DefaultProviderId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DefaultModelId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    DefaultReasoningEffort = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    DefaultCycleMode = table.Column<int>(type: "INTEGER", nullable: false),
                    DefaultCycleSessionMode = table.Column<int>(type: "INTEGER", nullable: false),
                    DefaultMaxCycles = table.Column<int>(type: "INTEGER", nullable: false),
                    DefaultCycleReviewPrompt = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Agents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Agents_Providers_DefaultProviderId",
                        column: x => x.DefaultProviderId,
                        principalTable: "Providers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "JobTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    GoalPrompt = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    ProviderId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ModelId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ReasoningEffort = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Branch = table.Column<string>(type: "TEXT", maxLength: 250, nullable: true),
                    GitChangeDeliveryMode = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetBranch = table.Column<string>(type: "TEXT", maxLength: 250, nullable: true),
                    CycleMode = table.Column<int>(type: "INTEGER", nullable: false),
                    CycleSessionMode = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxCycles = table.Column<int>(type: "INTEGER", nullable: false),
                    CycleReviewPrompt = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UseCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobTemplates_Providers_ProviderId",
                        column: x => x.ProviderId,
                        principalTable: "Providers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ProjectProviders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProviderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    PreferredModelId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    PreferredReasoningEffort = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectProviders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectProviders_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProjectProviders_Providers_ProviderId",
                        column: x => x.ProviderId,
                        principalTable: "Providers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProviderModels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProviderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ModelId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsAvailable = table.Column<bool>(type: "INTEGER", nullable: false),
                    PriceMultiplier = table.Column<decimal>(type: "TEXT", nullable: true),
                    MaxContextTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    MaxOutputTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RetiresOn = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderModels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProviderModels_Providers_ProviderId",
                        column: x => x.ProviderId,
                        principalTable: "Providers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProviderUsageSummaries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProviderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TotalInputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalOutputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalCostUsd = table.Column<decimal>(type: "TEXT", nullable: false),
                    TotalJobsCompleted = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalPremiumRequestsConsumed = table.Column<int>(type: "INTEGER", nullable: false),
                    LimitType = table.Column<string>(type: "TEXT", nullable: false),
                    CurrentUsage = table.Column<int>(type: "INTEGER", nullable: true),
                    MaxUsage = table.Column<int>(type: "INTEGER", nullable: true),
                    LimitResetTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsLimitReached = table.Column<bool>(type: "INTEGER", nullable: false),
                    LimitMessage = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    LimitWindowsJson = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    ConfiguredMaxUsage = table.Column<int>(type: "INTEGER", nullable: true),
                    CliVersion = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    VersionCheckedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PeriodStart = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastUpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastJobStartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NextExecutionAvailableAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ConsecutiveRateLimitCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastRateLimitAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastRateLimitMessage = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderUsageSummaries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProviderUsageSummaries_Providers_ProviderId",
                        column: x => x.ProviderId,
                        principalTable: "Providers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentSkills",
                columns: table => new
                {
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SkillId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentSkills", x => new { x.AgentId, x.SkillId });
                    table.ForeignKey(
                        name: "FK_AgentSkills_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AgentSkills_Skills_SkillId",
                        column: x => x.SkillId,
                        principalTable: "Skills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JobSchedules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ScheduleType = table.Column<string>(type: "TEXT", nullable: false),
                    ExecutionTarget = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    InferenceProviderId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Prompt = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    ModelId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    IdeaCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Frequency = table.Column<string>(type: "TEXT", nullable: false),
                    IntervalMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    HourUtc = table.Column<int>(type: "INTEGER", nullable: false),
                    MinuteUtc = table.Column<int>(type: "INTEGER", nullable: false),
                    WeeklyDay = table.Column<string>(type: "TEXT", nullable: false),
                    DayOfMonth = table.Column<int>(type: "INTEGER", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NextRunAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastRunAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobSchedules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobSchedules_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_JobSchedules_InferenceProviders_InferenceProviderId",
                        column: x => x.InferenceProviderId,
                        principalTable: "InferenceProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_JobSchedules_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_JobSchedules_Providers_ProviderId",
                        column: x => x.ProviderId,
                        principalTable: "Providers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ProjectAgents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProviderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PreferredModelId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    PreferredReasoningEffort = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectAgents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectAgents_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProjectAgents_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProjectAgents_Providers_ProviderId",
                        column: x => x.ProviderId,
                        principalTable: "Providers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    GoalPrompt = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    AttachedFilesJson = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProviderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IsScheduled = table.Column<bool>(type: "INTEGER", nullable: false),
                    JobScheduleId = table.Column<Guid>(type: "TEXT", nullable: true),
                    JobTemplateId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ScheduledForUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ModelUsed = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ReasoningEffort = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    PlanningOutput = table.Column<string>(type: "TEXT", nullable: true),
                    PlanningProviderId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PlanningModelUsed = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    PlanningReasoningEffortUsed = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    PlanningGeneratedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ExecutionPlan = table.Column<string>(type: "TEXT", nullable: true),
                    ActiveExecutionIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSwitchReason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    LastSwitchAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Branch = table.Column<string>(type: "TEXT", maxLength: 250, nullable: true),
                    GitChangeDeliveryMode = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetBranch = table.Column<string>(type: "TEXT", maxLength: 250, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Output = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ResumeFromStatus = table.Column<int>(type: "INTEGER", nullable: true),
                    RecoveryCheckpointAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RecoveryPrompt = table.Column<string>(type: "TEXT", nullable: true),
                    ResumeAttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastResumeAttemptAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastResumeFailureReason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    ForceFreshSession = table.Column<bool>(type: "INTEGER", nullable: false),
                    CancellationRequested = table.Column<bool>(type: "INTEGER", nullable: false),
                    CurrentActivity = table.Column<string>(type: "TEXT", nullable: true),
                    LastActivityAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    WorkerInstanceId = table.Column<string>(type: "TEXT", nullable: true),
                    LastHeartbeatAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RetryCount = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxRetries = table.Column<int>(type: "INTEGER", nullable: false),
                    ProcessId = table.Column<int>(type: "INTEGER", nullable: true),
                    CommandUsed = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    PlanningCommandUsed = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    ExecutionCommandUsed = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxExecutionMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                    MaxCostUsd = table.Column<decimal>(type: "TEXT", nullable: true),
                    MaxTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    StallTimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: true),
                    SuccessPattern = table.Column<string>(type: "TEXT", nullable: true),
                    FailurePattern = table.Column<string>(type: "TEXT", nullable: true),
                    Tags = table.Column<string>(type: "TEXT", nullable: true),
                    ParentJobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DependsOnJobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CycleMode = table.Column<int>(type: "INTEGER", nullable: false),
                    CycleSessionMode = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxCycles = table.Column<int>(type: "INTEGER", nullable: false),
                    CurrentCycle = table.Column<int>(type: "INTEGER", nullable: false),
                    CycleReviewPrompt = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    GitDiff = table.Column<string>(type: "TEXT", nullable: true),
                    GitCommitBefore = table.Column<string>(type: "TEXT", nullable: true),
                    SessionSummary = table.Column<string>(type: "TEXT", nullable: true),
                    GitCommitHash = table.Column<string>(type: "TEXT", nullable: true),
                    GitCheckpointStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    GitCheckpointBranch = table.Column<string>(type: "TEXT", maxLength: 250, nullable: true),
                    GitCheckpointBaseBranch = table.Column<string>(type: "TEXT", maxLength: 250, nullable: true),
                    GitCheckpointCommitHash = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    GitCheckpointReason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    GitCheckpointCapturedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PullRequestNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    PullRequestUrl = table.Column<string>(type: "TEXT", nullable: true),
                    PullRequestCreatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    MergedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ChangedFilesCount = table.Column<int>(type: "INTEGER", nullable: true),
                    BuildVerified = table.Column<bool>(type: "INTEGER", nullable: true),
                    BuildOutput = table.Column<string>(type: "TEXT", nullable: true),
                    ConsoleOutput = table.Column<string>(type: "TEXT", nullable: true),
                    PendingInteractionPrompt = table.Column<string>(type: "TEXT", nullable: true),
                    InteractionType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    InteractionChoices = table.Column<string>(type: "TEXT", nullable: true),
                    InteractionRequestedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IterationLoopId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SwarmId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    NotBeforeUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PlaywrightEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    EnvironmentsJson = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    EnvironmentCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Jobs_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Jobs_JobSchedules_JobScheduleId",
                        column: x => x.JobScheduleId,
                        principalTable: "JobSchedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Jobs_JobTemplates_JobTemplateId",
                        column: x => x.JobTemplateId,
                        principalTable: "JobTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Jobs_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Jobs_Providers_PlanningProviderId",
                        column: x => x.PlanningProviderId,
                        principalTable: "Providers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Jobs_Providers_ProviderId",
                        column: x => x.ProviderId,
                        principalTable: "Providers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Ideas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: false),
                    ExpandedDescription = table.Column<string>(type: "TEXT", maxLength: 20000, nullable: true),
                    ExpansionStatus = table.Column<string>(type: "TEXT", nullable: false),
                    ExpansionError = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    ExpandedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    IsProcessing = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Ideas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Ideas_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Ideas_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IterationLoops",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    InferenceProviderId = table.Column<Guid>(type: "TEXT", nullable: true),
                    InferenceModelId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ProviderId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ModelId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    MaxIterations = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxTotalCostUsd = table.Column<decimal>(type: "TEXT", nullable: true),
                    MaxConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    CooldownSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    AutoCommit = table.Column<bool>(type: "INTEGER", nullable: false),
                    AutoPush = table.Column<bool>(type: "INTEGER", nullable: false),
                    CompletedIterations = table.Column<int>(type: "INTEGER", nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalCostUsd = table.Column<decimal>(type: "TEXT", nullable: false),
                    CurrentJobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CurrentIdeaId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastIterationAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    StoppedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NextIterationAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastStopReason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    LastUsageCheckResult = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IterationLoops", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IterationLoops_Jobs_CurrentJobId",
                        column: x => x.CurrentJobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_IterationLoops_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JobChangeSets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FollowUpIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    GitCommitHash = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    GitCommitBefore = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ChangedFilesCount = table.Column<int>(type: "INTEGER", nullable: true),
                    SessionSummary = table.Column<string>(type: "TEXT", nullable: true),
                    PullRequestNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    PullRequestUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    MergedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    BuildVerified = table.Column<bool>(type: "INTEGER", nullable: true),
                    ModelUsed = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobChangeSets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobChangeSets_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JobExecutionStatistics",
                columns: table => new
                {
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InputTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    OutputTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    CostUsd = table.Column<decimal>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobExecutionStatistics", x => x.JobId);
                    table.ForeignKey(
                        name: "FK_JobExecutionStatistics_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JobMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Role = table.Column<string>(type: "TEXT", nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ToolName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ToolInput = table.Column<string>(type: "TEXT", nullable: true),
                    ToolOutput = table.Column<string>(type: "TEXT", nullable: true),
                    TokenCount = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobMessages_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JobPlanningStatistics",
                columns: table => new
                {
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InputTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    OutputTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    CostUsd = table.Column<decimal>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobPlanningStatistics", x => x.JobId);
                    table.ForeignKey(
                        name: "FK_JobPlanningStatistics_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JobProviderAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProviderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProviderName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ModelId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    AttemptOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    WasSuccessful = table.Column<bool>(type: "INTEGER", nullable: false),
                    AttemptedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobProviderAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobProviderAttempts_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JobStatistics",
                columns: table => new
                {
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExecutionDurationSeconds = table.Column<double>(type: "REAL", nullable: true),
                    TotalCostUsd = table.Column<decimal>(type: "TEXT", nullable: true),
                    InputTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    OutputTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    IsTokenEstimate = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobStatistics", x => x.JobId);
                    table.ForeignKey(
                        name: "FK_JobStatistics_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProviderUsageRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProviderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    InputTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    OutputTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    CostUsd = table.Column<decimal>(type: "TEXT", nullable: true),
                    PremiumRequestsConsumed = table.Column<int>(type: "INTEGER", nullable: true),
                    ModelUsed = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    DetectedLimitType = table.Column<string>(type: "TEXT", nullable: true),
                    DetectedCurrentUsage = table.Column<int>(type: "INTEGER", nullable: true),
                    DetectedMaxUsage = table.Column<int>(type: "INTEGER", nullable: true),
                    DetectedResetTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DetectedLimitReached = table.Column<bool>(type: "INTEGER", nullable: false),
                    RawLimitMessage = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    DetectedLimitWindowsJson = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    RecordedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderUsageRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProviderUsageRecords_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ProviderUsageRecords_Providers_ProviderId",
                        column: x => x.ProviderId,
                        principalTable: "Providers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IdeaAttachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    IdeaId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    RelativePath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdeaAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IdeaAttachments_Ideas_IdeaId",
                        column: x => x.IdeaId,
                        principalTable: "Ideas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Agents_DefaultProviderId",
                table: "Agents",
                column: "DefaultProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_Agents_Name",
                table: "Agents",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentSkills_SkillId",
                table: "AgentSkills",
                column: "SkillId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetRoleClaims_RoleId",
                table: "AspNetRoleClaims",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                table: "AspNetRoles",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserClaims_UserId",
                table: "AspNetUserClaims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserLogins_UserId",
                table: "AspNetUserLogins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserRoles_RoleId",
                table: "AspNetUserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "AspNetUsers",
                column: "NormalizedUserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CriticalErrorLogs_CreatedAt",
                table: "CriticalErrorLogs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_CriticalErrorLogs_Source_CreatedAt",
                table: "CriticalErrorLogs",
                columns: new[] { "Source", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_IdeaAttachments_IdeaId",
                table: "IdeaAttachments",
                column: "IdeaId");

            migrationBuilder.CreateIndex(
                name: "IX_Ideas_CreatedAt",
                table: "Ideas",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Ideas_JobId",
                table: "Ideas",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_Ideas_ProjectId",
                table: "Ideas",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Ideas_SortOrder",
                table: "Ideas",
                column: "SortOrder");

            migrationBuilder.CreateIndex(
                name: "IX_InferenceModels_InferenceProviderId_ModelId_TaskType",
                table: "InferenceModels",
                columns: new[] { "InferenceProviderId", "ModelId", "TaskType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InferenceProviders_Name",
                table: "InferenceProviders",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IterationLoops_CurrentJobId",
                table: "IterationLoops",
                column: "CurrentJobId");

            migrationBuilder.CreateIndex(
                name: "IX_IterationLoops_ProjectId",
                table: "IterationLoops",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_IterationLoops_Status",
                table: "IterationLoops",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_JobChangeSets_JobId",
                table: "JobChangeSets",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_JobChangeSets_JobId_FollowUpIndex",
                table: "JobChangeSets",
                columns: new[] { "JobId", "FollowUpIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_JobMessages_CreatedAt",
                table: "JobMessages",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_JobMessages_JobId",
                table: "JobMessages",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_JobProviderAttempts_JobId",
                table: "JobProviderAttempts",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_JobProviderAttempts_JobId_AttemptOrder",
                table: "JobProviderAttempts",
                columns: new[] { "JobId", "AttemptOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_AgentId",
                table: "Jobs",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_CreatedAt",
                table: "Jobs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_JobScheduleId_ScheduledForUtc",
                table: "Jobs",
                columns: new[] { "JobScheduleId", "ScheduledForUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_JobTemplateId",
                table: "Jobs",
                column: "JobTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_PlanningProviderId",
                table: "Jobs",
                column: "PlanningProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_ProjectId",
                table: "Jobs",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_ProviderId",
                table: "Jobs",
                column: "ProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_Status",
                table: "Jobs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_SwarmId",
                table: "Jobs",
                column: "SwarmId");

            migrationBuilder.CreateIndex(
                name: "IX_JobSchedules_AgentId",
                table: "JobSchedules",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_JobSchedules_InferenceProviderId",
                table: "JobSchedules",
                column: "InferenceProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_JobSchedules_IsEnabled_NextRunAtUtc",
                table: "JobSchedules",
                columns: new[] { "IsEnabled", "NextRunAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_JobSchedules_ProjectId_IsEnabled",
                table: "JobSchedules",
                columns: new[] { "ProjectId", "IsEnabled" });

            migrationBuilder.CreateIndex(
                name: "IX_JobSchedules_ProviderId",
                table: "JobSchedules",
                column: "ProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_JobTemplates_CreatedAt",
                table: "JobTemplates",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_JobTemplates_Name",
                table: "JobTemplates",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobTemplates_ProviderId",
                table: "JobTemplates",
                column: "ProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectAgents_AgentId",
                table: "ProjectAgents",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectAgents_ProjectId_AgentId",
                table: "ProjectAgents",
                columns: new[] { "ProjectId", "AgentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectAgents_ProviderId",
                table: "ProjectAgents",
                column: "ProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectEnvironments_ProjectId_Name",
                table: "ProjectEnvironments",
                columns: new[] { "ProjectId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectEnvironments_ProjectId_SortOrder",
                table: "ProjectEnvironments",
                columns: new[] { "ProjectId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectProviders_ProjectId_Priority",
                table: "ProjectProviders",
                columns: new[] { "ProjectId", "Priority" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectProviders_ProjectId_ProviderId",
                table: "ProjectProviders",
                columns: new[] { "ProjectId", "ProviderId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectProviders_ProviderId",
                table: "ProjectProviders",
                column: "ProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_Projects_Name",
                table: "Projects",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProviderModels_ProviderId_ModelId",
                table: "ProviderModels",
                columns: new[] { "ProviderId", "ModelId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Providers_Name",
                table: "Providers",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProviderUsageRecords_JobId",
                table: "ProviderUsageRecords",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderUsageRecords_ProviderId",
                table: "ProviderUsageRecords",
                column: "ProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderUsageRecords_RecordedAt",
                table: "ProviderUsageRecords",
                column: "RecordedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderUsageSummaries_ProviderId",
                table: "ProviderUsageSummaries",
                column: "ProviderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Skills_Name",
                table: "Skills",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentSkills");

            migrationBuilder.DropTable(
                name: "AppSettings");

            migrationBuilder.DropTable(
                name: "AspNetRoleClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserLogins");

            migrationBuilder.DropTable(
                name: "AspNetUserRoles");

            migrationBuilder.DropTable(
                name: "AspNetUserTokens");

            migrationBuilder.DropTable(
                name: "CriticalErrorLogs");

            migrationBuilder.DropTable(
                name: "IdeaAttachments");

            migrationBuilder.DropTable(
                name: "InferenceModels");

            migrationBuilder.DropTable(
                name: "IterationLoops");

            migrationBuilder.DropTable(
                name: "JobChangeSets");

            migrationBuilder.DropTable(
                name: "JobExecutionStatistics");

            migrationBuilder.DropTable(
                name: "JobMessages");

            migrationBuilder.DropTable(
                name: "JobPlanningStatistics");

            migrationBuilder.DropTable(
                name: "JobProviderAttempts");

            migrationBuilder.DropTable(
                name: "JobStatistics");

            migrationBuilder.DropTable(
                name: "ProjectAgents");

            migrationBuilder.DropTable(
                name: "ProjectEnvironments");

            migrationBuilder.DropTable(
                name: "ProjectProviders");

            migrationBuilder.DropTable(
                name: "ProviderModels");

            migrationBuilder.DropTable(
                name: "ProviderUsageRecords");

            migrationBuilder.DropTable(
                name: "ProviderUsageSummaries");

            migrationBuilder.DropTable(
                name: "Skills");

            migrationBuilder.DropTable(
                name: "AspNetRoles");

            migrationBuilder.DropTable(
                name: "AspNetUsers");

            migrationBuilder.DropTable(
                name: "Ideas");

            migrationBuilder.DropTable(
                name: "Jobs");

            migrationBuilder.DropTable(
                name: "JobSchedules");

            migrationBuilder.DropTable(
                name: "JobTemplates");

            migrationBuilder.DropTable(
                name: "Agents");

            migrationBuilder.DropTable(
                name: "InferenceProviders");

            migrationBuilder.DropTable(
                name: "Projects");

            migrationBuilder.DropTable(
                name: "Providers");
        }
    }
}
