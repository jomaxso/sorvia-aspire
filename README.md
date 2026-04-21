# Sorvia Aspire

A collection of .NET Aspire hosting integrations by [Sorvia](https://github.com/jomaxso).

## Packages

| Package | Description | Version |
|---------|-------------|---------|
| [Sorvia.Aspire.Hosting.Dokploy](src/Sorvia.Aspire.Hosting.Dokploy/) | Deploy .NET Aspire apps to a self-hosted [Dokploy](https://dokploy.com) instance | 0.1.0 |

## Quick Start

Add the package to your AppHost project:

```shell
dotnet add package Sorvia.Aspire.Hosting.Dokploy
```

Then register the Dokploy deployment target:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.Api>("api");

builder.AddDokployEnvironment("dokploy");

builder.Build().Run();
```

When running locally, the Dokploy resource is a no-op — everything runs as usual. When publishing (`dotnet run --publisher dokploy`), the integration generates Docker Compose artifacts and deploys them to your Dokploy instance via the REST API.

## Repository Structure

```
src/
  Sorvia.Aspire.Hosting.Dokploy/   # Dokploy hosting integration package
demo/
  Apphost/                          # Sample AppHost showing usage
```

## Prerequisites

- .NET 10 SDK
- A running [Dokploy](https://dokploy.com) instance
- A Dokploy API key (Settings → API in the Dokploy panel)

## Building

```shell
dotnet build
```

## Packaging

Before the first publish, verify the package locally:

```shell
dotnet pack -c Release -o ./artifacts src/Sorvia.Aspire.Hosting.Dokploy/Sorvia.Aspire.Hosting.Dokploy.csproj
```

GitHub Actions is split into:

- `ci.yml` for restore, build, and pack validation
- `publish.yml` for automatic publishing to nuget.org when the package version changes on `main`

## NuGet Trusted Publishing

The repository is set up for **nuget.org trusted publishing** with GitHub Actions OIDC.

Create the trusted publishing policy on nuget.org with these exact values:

| Setting | Value |
|---------|-------|
| Repository Owner | `jomaxso` |
| Repository | `sorvia-aspire` |
| Workflow File | `publish.yml` |
| Environment | *(leave blank)* |

In GitHub, add this repository secret:

| Secret | Value |
|--------|-------|
| `NUGET_USER` | nuget.org username |

Release flow:

```shell
# bump <Version> in the package project
git add src/Sorvia.Aspire.Hosting.Dokploy/Sorvia.Aspire.Hosting.Dokploy.csproj
git commit -m "Bump package version to 0.1.0"
git push origin main
```

## License

[MIT](LICENSE)
