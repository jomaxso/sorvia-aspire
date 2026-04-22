# Sorvia Aspire

A collection of .NET Aspire hosting integrations by [Sorvia](https://github.com/jomaxso).

## Packages

| Package | Description | NuGet |
|---------|-------------|-------|
| [Sorvia.Aspire.Hosting.Dokploy](src/Sorvia.Aspire.Hosting.Dokploy/) | Deploy .NET Aspire apps to a self-hosted [Dokploy](https://dokploy.com) instance | [![NuGet](https://img.shields.io/nuget/v/Sorvia.Aspire.Hosting.Dokploy)](https://www.nuget.org/packages/Sorvia.Aspire.Hosting.Dokploy) |

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
The deploy prompt also asks for the target Dokploy environment and offers `production` as the default; empty input falls back to `production`.
If you enter the Dokploy host without `http://` or `https://`, the deploy flow assumes `https://`.
Application domains are created automatically for external HTTP/HTTPS endpoints; you can control that on resources with Aspire methods such as `.WithExternalHttpEndpoints()`. The package README documents the full domain and registry-domain rules.
See [`src/Sorvia.Aspire.Hosting.Dokploy/README.md`](src/Sorvia.Aspire.Hosting.Dokploy/README.md) for the full package guide and API reference.

## Repository Structure

```
src/
  Sorvia.Aspire.Hosting.Dokploy/   # Dokploy hosting integration package
demo/
  demo.AppHost/                    # Sample Aspire AppHost
  demo.Server/                     # Sample backend
  frontend/                        # Sample Vite frontend
```

## Prerequisites

- .NET 10 SDK
- A running [Dokploy](https://dokploy.com) instance
- A Dokploy API key (Settings → API in the Dokploy panel)

## Building

```shell
dotnet build src/Sorvia.Aspire.Hosting.Dokploy/Sorvia.Aspire.Hosting.Dokploy.csproj -c Release
dotnet build demo/demo.AppHost/demo.AppHost.csproj -c Release
```

## Packaging

Before the first publish, verify the package locally:

```shell
dotnet pack src/Sorvia.Aspire.Hosting.Dokploy/Sorvia.Aspire.Hosting.Dokploy.csproj -c Release -o ./artifacts
```

GitHub Actions is split into:

- `.github/workflows/ci.yml` for restore, build, pack validation, demo AppHost validation, and package artifact upload
- `.github/workflows/publish.yml` for automatic publishing to nuget.org after a successful `CI` run on `main`, when the package version changed

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
git commit -m "Bump package version to <new-version>"
git push origin main
```

## License

[MIT](LICENSE)
