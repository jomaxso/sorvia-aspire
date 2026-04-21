# Sorvia.Aspire.Hosting.Dokploy

[![NuGet](https://img.shields.io/nuget/v/Sorvia.Aspire.Hosting.Dokploy)](https://www.nuget.org/packages/Sorvia.Aspire.Hosting.Dokploy)

A .NET Aspire hosting integration for [Dokploy](https://dokploy.com) — a free, self-hostable PaaS. Deploy your entire Aspire application to a Dokploy instance with a single method call.

## Getting Started

### Prerequisites

- .NET 10 SDK
- A running [Dokploy](https://dokploy.com) instance
- A Dokploy API key (Settings → API in the Dokploy panel)

### Installation

```shell
dotnet add package Sorvia.Aspire.Hosting.Dokploy
```

Or add the package reference directly:

```xml
<PackageReference Include="Sorvia.Aspire.Hosting.Dokploy" Version="0.1.0" />
```

### Basic Usage

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject<Projects.ApiService>("api");
var frontend = builder.AddNpmApp("portal", "../RoadmapPortal", "dev");

// Deploy everything to Dokploy in publish mode
builder.AddDokployEnvironment("dokploy");

builder.Build().Run();
```

`AddDokployEnvironment` auto-detects **all resources** in the Aspire application model and handles deployment when the AppHost runs in publish mode (`dotnet run --publisher dokploy`). In run mode the Dokploy environment is not added to the model — everything runs locally as usual.

## Configuration

`AddDokployEnvironment("my-project")` automatically creates Aspire parameters for:

| Parameter | Description |
|-----------|-------------|
| `dokploy-url` | Base URL of the Dokploy instance; if the scheme is omitted, `https://` is assumed |
| `dokploy-api-key` | API key (secret) |
| `dokploy-project-name` | Target Dokploy project name |
| `dokploy-environment` | Target Dokploy environment inside the project (defaults to `production`) |

When `aspire deploy` runs, Aspire prompts for those values and stores them in deployment state. The Dokploy environment prompt is prefilled with `production`, and an empty value also falls back to `production`. For `dokploy-url`, a host name without `http://` or `https://` is treated as `https://...`. Re-runs reuse the saved values. Plain `aspire publish` can still generate Docker Compose artifacts without Dokploy credentials.

The name passed to `AddDokployEnvironment(...)` is the Aspire resource name. The actual Dokploy project name comes from the `dokploy-project-name` parameter. The Dokploy environment inside that project comes from the `dokploy-environment` parameter and defaults to `production`.

### Optional Settings

```csharp
builder.AddDokployEnvironment("my-roadmap")
    .WithServerId("server-123")   // target a specific Dokploy server
    .WithDashboard(true);         // enabled by default
```

## Aspire Dashboard

The [Aspire Dashboard](https://learn.microsoft.com/dotnet/aspire/fundamentals/dashboard/overview) is deployed as a container by default. All other services are automatically configured to send OpenTelemetry (OTLP) telemetry to the dashboard.

```csharp
// Dashboard enabled by default
builder.AddDokployEnvironment("dokploy");

// Explicitly disable the dashboard
builder.AddDokployEnvironment("dokploy").WithDashboard(false);
```

When enabled:
- Dashboard web UI on port **18888**
- OTLP gRPC endpoint on port **18889** (compose-internal)
- All services receive `OTEL_EXPORTER_OTLP_ENDPOINT` automatically

## Container Registry

Container images built from `ProjectResource` instances need a registry so the Dokploy server can pull them.

**Without explicit configuration**, the integration bootstraps a **project-scoped private registry on Dokploy** automatically — creating a `registry:2` compose stack, configuring an `sslip.io` domain with Let's Encrypt, registering it in Dokploy, and pushing the built images.

## Domain Management

Application domains are managed automatically during publish-mode deploys.

- A domain is created for a resource only when it exposes an external `http` or `https` endpoint. In practice this usually means opting the resource into public endpoints with Aspire methods such as `.WithExternalHttpEndpoints()`. The Aspire Dashboard is treated as public by default and is included in the same logic.
- If a resource no longer has a managed external `http`/`https` endpoint, previously managed Dokploy domains for that application are removed.
- If the preferred host already exists on the Dokploy application, no new domain is created.
- The preferred host is derived from the Dokploy server host and the Aspire project/resource names. If that host does not resolve, the integration falls back to an `sslip.io` hostname.
- Application domains are created with HTTPS enabled.

Example:

```csharp
var api = builder.AddProject<Projects.ApiService>("api")
    .WithExternalHttpEndpoints();
```

Without an external HTTP/HTTPS endpoint, the resource is still deployed, but no public Dokploy domain is created for it.

The project-scoped registry follows a separate rule set:

- A registry domain is created only when the integration has to bootstrap its own Dokploy registry.
- Auto-registry bootstrap happens only when no default container registry is configured on the Dokploy environment and no application has its own explicit container registry reference.
- If the registry compose service already has the expected host, the existing domain is reused instead of creating another one.
- Registry domains use `sslip.io` with Let's Encrypt because the managed registry is exposed through a Dokploy compose service.

### Explicit Registry

```csharp
// Default registry for all resources
var registry = builder.AddContainerRegistry("ghcr", "ghcr.io", "myorg");
builder.AddDokployEnvironment("dokploy")
    .WithContainerRegistry(registry);

// Per-resource registry
builder.AddProject<Projects.ApiService>("api")
    .WithContainerRegistry(registry);
```

### Parameterized Registry (CI/CD)

```csharp
var registryEndpoint = builder.AddParameter("registry-endpoint");
var registryRepo = builder.AddParameter("registry-repo");
var registry = builder.AddContainerRegistry("registry", registryEndpoint, registryRepo);

builder.AddDokployEnvironment("dokploy")
    .WithContainerRegistry(registry);
```

## Native Database Resources

Dokploy provides built-in support for PostgreSQL, Redis, MySQL, MariaDB, and MongoDB. Two approaches are available:

### Approach 1: Dokploy-Specific Resources

Local Docker containers in run mode, Dokploy-native databases in publish mode:

```csharp
var postgres = builder.AddDokployPostgres("postgres").WithDataVolume();
var db = postgres.AddDatabase("mydb");
var redis = builder.AddDokployRedis("redis");
var mysql = builder.AddDokployMySql("mysql");
var mariadb = builder.AddDokployMariaDB("mariadb");
var mongo = builder.AddDokployMongoDB("mongo");

builder.AddProject<Projects.ApiService>("api")
    .WithReference(db)
    .WithReference(redis);

builder.AddDokployEnvironment("dokploy");
```

### Approach 2: Opt-In from Standard Aspire Resources

```csharp
var postgres = builder.AddPostgres("postgres").PublishAsDokployDatabase();
var db = postgres.AddDatabase("mydb");
var redis = builder.AddRedis("redis").PublishAsDokployDatabase();
var mysql = builder.AddMySql("mysql").PublishAsDokployDatabase();
var mongo = builder.AddMongoDB("mongo").PublishAsDokployDatabase();

// MariaDB needs a dedicated method since it shares MySqlServerResource
var mariadb = builder.AddMySql("mariadb").PublishAsDokployMariaDB();

builder.AddDokployEnvironment("dokploy");
```

### Database Credential Customization

Aspire's standard configuration methods are respected and forwarded to the Dokploy API:

```csharp
var customUser = builder.AddParameter("pg-user");
var customPassword = builder.AddParameter("pg-password", secret: true);

var postgres = builder.AddDokployPostgres("postgres")
    .WithUserName(customUser)
    .WithPassword(customPassword);
```

| Database   | Forwarded Properties | Defaults |
|------------|----------------------|----------|
| PostgreSQL | `UserNameParameter`, `PasswordParameter`, Docker image | User: `postgres`, password: auto-generated |
| Redis      | `PasswordParameter`, Docker image | No default password |
| MySQL      | `PasswordParameter`, Docker image | Password: auto-generated |
| MariaDB    | `PasswordParameter`, Docker image | Password: auto-generated |
| MongoDB    | `UserNameParameter`, `PasswordParameter`, Docker image | No default credentials |

### Lifecycle Methods

Control run-mode vs. publish-mode behavior, mirroring the `Aspire.Hosting.Azure` pattern:

```csharp
// Run mode: connect to an existing database instead of a local container
builder.AddDokployPostgres("postgres").RunAsExisting("Host=localhost;Port=5432;...");

// Parameter-based (recommended)
var connStr = builder.AddParameter("pg-conn", secret: true);
builder.AddDokployPostgres("postgres").RunAsExisting(connStr);

// Publish mode: connect to an existing Dokploy-provisioned database
builder.AddDokployPostgres("postgres").PublishAsExisting(connStr);
```

**Default behavior** (no lifecycle methods): local Docker containers in run mode, Dokploy-native provisioning in publish mode.

### Dokploy Network Integration

When native databases are used, the generated Docker Compose file automatically declares `dokploy-network` as an external network and attaches all services to it, enabling secure internal communication with Dokploy-managed databases.

## Complete Example

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var registry = builder.AddContainerRegistry("ghcr", "ghcr.io", "myorg");

var postgres = builder.AddDokployPostgres("postgres").WithDataVolume();
var db = postgres.AddDatabase("roadmapdb");
var redis = builder.AddDokployRedis("redis");

var api = builder.AddProject<Projects.ApiService>("api")
    .WithReference(db)
    .WithReference(redis);

var portal = builder.AddNpmApp("portal", "../RoadmapPortal", "dev")
    .WithReference(api)
    .WithHttpEndpoint(env: "PORT");

builder.AddDokployEnvironment("requirement-roadmap")
    .WithContainerRegistry(registry)
    .WithDashboard(true);

builder.Build().Run();
```

## How It Works

The integration follows the Docker Compose publisher shape from `Aspire.Hosting.Docker`, replacing the final delivery stage with Dokploy orchestration:

1. **`publish-{name}`** — Generates Docker Compose artifacts (images, env vars, ports, volumes, service dependencies, `.env` file, Aspire Dashboard).
2. **`prepare-{name}`** — Runs the stock Docker Compose prepare step for `.env` capture and compose preparation.
3. **`deploy-{name}`** — Deploys to Dokploy via REST API: validates config, finds/creates a project, bootstraps a registry (if needed), provisions native databases, creates/updates applications, synchronizes domains, pushes images, and triggers deployments.

The publish and prepare steps reuse `Aspire.Hosting.Docker` internals. The compose object model (`ComposeFile`, `Service`, `Network`, `Volume`) is also used for Dokploy-managed stacks such as the project registry.

## API Reference

### Environment Extensions

| Method | Description |
|--------|-------------|
| `AddDokployEnvironment(name)` | Adds a Dokploy deployment target (publish mode only) |
| `.WithServerId(string\|parameter)` | Target a specific Dokploy server |
| `.WithDashboard(bool)` | Enable/disable the Aspire Dashboard container (default: `true`) |
| `.WithContainerRegistry(registry)` | Set a default container registry for all resources |
| `.ConfigureComposeFile(Action<ComposeFile>)` | Customize the generated Docker Compose file |
| `.ConfigureEnvFile(Action<IDictionary<...>>)` | Customize captured environment variables |
| `.WithProperties(Action<DokployEnvironmentResource>)` | Configure resource properties directly |

### Database Extensions

| Method | Description |
|--------|-------------|
| `AddDokployPostgres(name)` | PostgreSQL (local in run, Dokploy-native in publish) |
| `AddDokployRedis(name)` | Redis (local in run, Dokploy-native in publish) |
| `AddDokployMySql(name)` | MySQL (local in run, Dokploy-native in publish) |
| `AddDokployMariaDB(name)` | MariaDB (local in run, Dokploy-native in publish) |
| `AddDokployMongoDB(name)` | MongoDB (local in run, Dokploy-native in publish) |
| `.PublishAsDokployDatabase()` | Opt a standard Aspire database into Dokploy-native provisioning |
| `.PublishAsDokployMariaDB()` | Opt a MySQL resource into Dokploy MariaDB provisioning |
| `.RunAsExisting(string\|parameter)` | Connect to existing DB in run mode |
| `.PublishAsExisting(string\|parameter)` | Connect to existing DB in publish mode |

## Dokploy API Compatibility

This library targets the Dokploy REST API (tRPC-based). Authentication uses the `x-api-key` header.

<details>
<summary>Endpoints used</summary>

| Endpoint | Purpose |
|----------|---------|
| `GET /api/project.all` | List existing projects |
| `POST /api/project.create` | Create a new project |
| `POST /api/environment.create` | Create an environment inside a project |
| `GET /api/application.search` | Find existing applications by name |
| `POST /api/application.create` | Create application shells |
| `POST /api/application.saveDockerProvider` | Bind container images |
| `POST /api/application.saveEnvironment` | Save environment variables |
| `POST /api/application.update` | Link applications to a registry |
| `POST /api/application.deploy` | Trigger application deploys |
| `POST /api/compose.create` | Create a Docker Compose service |
| `GET /api/compose.search` | Find project compose services |
| `POST /api/compose.update` | Upload compose content |
| `POST /api/compose.deploy` | Deploy a compose service |
| `GET /api/domain.byComposeId` | Check compose domain existence |
| `GET /api/domain.byApplicationId` | Read application domains |
| `POST /api/domain.create` | Create application or registry domains |
| `POST /api/domain.remove` | Remove managed application domains |
| `GET /api/registry.all` | List Dokploy registries |
| `POST /api/registry.create` | Register a container registry |
| `POST /api/registry.update` | Update registry credentials |
| `POST /api/postgres.create` | Provision PostgreSQL |
| `GET /api/postgres.one` | Read PostgreSQL connection details |
| `POST /api/redis.create` | Provision Redis |
| `GET /api/redis.one` | Read Redis connection details |
| `POST /api/mysql.create` | Provision MySQL |
| `GET /api/mysql.one` | Read MySQL connection details |
| `POST /api/mariadb.create` | Provision MariaDB |
| `GET /api/mariadb.one` | Read MariaDB connection details |
| `POST /api/mongo.create` | Provision MongoDB |
| `GET /api/mongo.one` | Read MongoDB connection details |

</details>

## License

MIT
