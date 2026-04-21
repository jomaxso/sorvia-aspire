# AGENTS

Guidance for automated agents working in this repository.

## Repository purpose

This repository hosts the NuGet package **`Sorvia.Aspire.Hosting.Dokploy`**, a .NET Aspire hosting integration for deploying applications to Dokploy.

## Repository layout

- `src/Sorvia.Aspire.Hosting.Dokploy/` — the actual package source
- `demo/demo.AppHost/` — sample Aspire AppHost that references the package project
- `demo/demo.Server/` — sample backend used by the demo
- `demo/frontend/` — sample frontend used by the demo
- `.github/workflows/` — CI, publish, and PR-labeler workflows

## Working rules

- Prefer **surgical changes** in `src/Sorvia.Aspire.Hosting.Dokploy/`.
- Only change the demo when package behavior or usage examples require it.
- Keep documentation aligned when public package behavior changes.
- Do not introduce unrelated refactors while touching package code.

## Validation commands

Use these commands from the repository root:

```powershell
dotnet build .\src\Sorvia.Aspire.Hosting.Dokploy\Sorvia.Aspire.Hosting.Dokploy.csproj -c Release
dotnet build .\demo\demo.AppHost\demo.AppHost.csproj -c Release
dotnet pack .\src\Sorvia.Aspire.Hosting.Dokploy\Sorvia.Aspire.Hosting.Dokploy.csproj -c Release -o .\artifacts
```

## Packaging and publishing

- Package ID: `Sorvia.Aspire.Hosting.Dokploy`
- Publish workflow: `.github/workflows/publish.yml`
- CI workflow: `.github/workflows/ci.yml`
- NuGet publishing uses **nuget.org trusted publishing (OIDC)**, not a long-lived API key.
- The publish workflow runs on pushes to `main` and publishes only when the package version changed.
- Before changing publish behavior, keep `README.md` in sync with the workflow and nuget.org setup instructions.

## Notes for code changes

- The package targets **.NET 10**.
- The demo AppHost is the fastest way to verify package wiring.
- There is currently no dedicated test project in the repo; build and pack validation are the main safety checks.
