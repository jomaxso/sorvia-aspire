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
<PackageReference Include="Sorvia.Aspire.Hosting.Dokploy" Version="0.1.3" />
```

### Basic Usage

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var server = builder.AddCSharpApp("server", "../demo.Server")
    .WithExternalHttpEndpoints();

var webfrontend = builder.AddViteApp("webfrontend", "../frontend")
    .WithReference(server)
    .WaitFor(server);

server.PublishWithContainerFiles(webfrontend, "wwwroot");

// High-level shortcut: create a Docker Compose environment and deploy it to Dokploy
builder.AddDokployEnvironment("demo");

builder.Build().Run();
```

`AddDokployEnvironment` is the high-level shortcut for a Docker Compose publishing environment whose deploy step targets Dokploy. If you already want to configure a Docker Compose environment explicitly, use the equivalent adapter form:

```csharp
builder.AddDockerComposeEnvironment("demo")
    .WithDokployDeploymentTarget();
```

Both forms reuse the Docker Compose publish/prepare pipeline and replace the final `docker compose up` deploy step with Dokploy REST API orchestration. The original Docker Compose step name may still appear as a short compatibility bridge so Aspire's generated build dependencies remain valid, but it does not run Docker Compose. In run mode the Dokploy deployment target is not active, so the application still runs locally as usual.
The explicit Docker Compose form mirrors the current demo application in this repository under `demo/demo.AppHost`.

`aspire destroy` is also wired to Dokploy. The package replaces the Docker Compose destroy step with named Dokploy cleanup phases that remove the deployed applications, their domains, Dokploy-native databases, and the auto-bootstrapped project registry. After those resources are gone, the Dokploy project itself is removed only when it no longer contains any services; if the project contains unrelated services, it is kept.

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

When the API key can access multiple Dokploy organizations, deployments target the **active organization** selected in Dokploy. The deployment summary includes that organization, and project lookup is scoped to it so same-named projects in other organizations are not reused accidentally.

### Optional Settings

```csharp
builder.AddDokployEnvironment("my-roadmap")
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

**Without explicit configuration**, the integration bootstraps a **project-scoped private registry on Dokploy** automatically — creating a `registry:2` compose stack, configuring an HTTPS domain with Let's Encrypt, registering it in Dokploy, and pushing the built images. If a Dokploy registry record for the project already exists, that registry is reused. If its stored URL does not match the existing registry compose domain, the existing registry record is updated to the existing domain only after that domain accepts HTTPS registry logins, instead of creating another registry or domain. Otherwise, the generated `sslip.io` registry host includes a short Dokploy project ID suffix so recreated projects with the same name do not collide with stale Traefik routes.

Images that already point at a pullable registry (for example Docker Hub, MCR, GHCR, or other public/private registries) continue to use that registry directly. Only images available locally are mirrored into the auto-bootstrapped project registry.

On the first deploy, the auto-bootstrapped registry may need a few minutes before the `sslip.io` host, Traefik route, and Let's Encrypt certificate are all ready for authenticated pushes.

Subsequent deploys are idempotent. When a matching Dokploy registry record already exists and accepts its credentials, it is reused without redeploying the registry compose service. If that registry record exists but the live registry rejects its credentials, the existing compose service is repaired and redeployed with the registry resource credentials; no new registry or domain is created. When the registry has not been registered yet, the compose service, domain, credentials, and Dokploy registry record are compared and only changed when required. Existing applications are also only redeployed when their image/provider, registry link, command, environment variables, domains, or deployment status require it.

## Domain Management

Application domains are managed automatically during publish-mode deploys.

- A domain is created for a resource only when it exposes an external `http` or `https` endpoint. In practice this usually means opting the resource into public endpoints with Aspire methods such as `.WithExternalHttpEndpoints()`. The Aspire Dashboard is treated as public by default and is included in the same logic.
- If a Dokploy application already has one or more domains, those domains are reused and no generated replacement domain is created.
- If a resource no longer has a managed external `http`/`https` endpoint, existing Dokploy domains are left untouched. `aspire destroy` still removes the application and its domains.
- The preferred host is derived from the Dokploy server host and the Aspire project/resource names. If that host does not resolve, the integration falls back to an `sslip.io` hostname.
- Application domains are created with HTTPS enabled.

Example:

```csharp
var server = builder.AddCSharpApp("server", "../MyServer")
    .WithExternalHttpEndpoints();
```

Without an external HTTP/HTTPS endpoint, the resource is still deployed, but no new public Dokploy domain is created for it. If the resource already has a Dokploy domain, the deployment summary continues to show it.

The project-scoped registry follows a separate rule set:

- A registry domain is created only when the integration has to bootstrap its own Dokploy registry.
- Auto-registry bootstrap happens only when no default container registry is configured on the Dokploy environment and no application has its own explicit container registry reference.
- If a matching Dokploy registry record already exists, that registry is reused. If its credentials no longer match the live registry compose service, the existing compose service is repaired with the registry resource credentials.
- If the registry compose service already has any domain, that existing domain is reused as the registry host instead of deriving or creating another one.
- Registry domains use `sslip.io` with Let's Encrypt because the managed registry is exposed through a Dokploy compose service.

### Explicit Registry

```csharp
// Default registry for all resources
var registry = builder.AddContainerRegistry("ghcr", "ghcr.io", "myorg");
builder.AddDokployEnvironment("dokploy")
    .WithContainerRegistry(registry);

// Per-resource registry
builder.AddCSharpApp("server", "../demo.Server")
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

builder.AddCSharpApp("server", "../demo.Server")
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
| MySQL      | `PasswordParameter`, Docker image | User: resource name, password: auto-generated |
| MariaDB    | `PasswordParameter`, Docker image | User: resource name, password: auto-generated |
| MongoDB    | `UserNameParameter`, `PasswordParameter`, Docker image | User: resource name, password: auto-generated |

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

var server = builder.AddCSharpApp("server", "../demo.Server")
    .WithReference(db)
    .WithReference(redis);

var webfrontend = builder.AddViteApp("webfrontend", "../frontend")
    .WithReference(server)
    .WaitFor(server);

server.PublishWithContainerFiles(webfrontend, "wwwroot");

builder.AddDokployEnvironment("demo")
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

For a detailed architecture walkthrough with Mermaid diagrams, see the [Dokploy deployment architecture guide](https://github.com/jomaxso/sorvia-aspire/blob/main/docs/dokploy-deployment-architecture.md).

## API Reference

### Environment Extensions

| Method | Description |
|--------|-------------|
| `AddDokployEnvironment(name)` | Adds a Docker Compose publishing environment that deploys to Dokploy |
| `.WithDokployDeploymentTarget()` | Converts an existing Docker Compose environment to deploy to Dokploy |
| `.WithDashboard(bool)` | Enable/disable the Aspire Dashboard container (default: `true`) |
| `.WithContainerRegistry(registry)` | Set a default container registry for all resources |
| `.ConfigureComposeFile(Action<ComposeFile>)` | Customize the generated Docker Compose file |
| `.ConfigureEnvFile(Action<IDictionary<...>>)` | Customize captured environment variables |

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
