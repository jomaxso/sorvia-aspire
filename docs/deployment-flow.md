# Dokploy Deployment Flow

High-level overview of the named Dokploy deployment phases created by `WithDokployDeploymentTarget`.

For a detailed architecture walkthrough with Mermaid diagrams, see [Dokploy Deployment Architecture](dokploy-deployment-architecture.md).

---

## 1. Validate Configuration

Pipeline step: `dokploy-validate-{name}`.

- Resolve **server URL** and **API key** from parameters (or use defaults).
- Fail early if either value is missing or blank.
- Normalize the server URL (ensure valid URI with scheme).
- Perform a connectivity check by calling `ListProjects` on the Dokploy API — if the server is unreachable, abort with a descriptive error.

## 2. Resolve Deployment Parameters

- Resolve the **project name** from the Dokploy deployment target parameter.
- Resolve the **deployment environment name** (e.g. `production`); normalize to lowercase, default to `"production"` when empty.
- Optionally resolve the **active organization** (used to scope project lookup).

## 3. Find or Create Dokploy Project & Environment

Pipeline step: `dokploy-project-{name}`.

- Query existing projects by name. If an organization is active, match within that organization.
- If no matching project exists → create a new one via the API.
- Within the project, find the target environment by name. If it doesn't exist → create it.

## 4. Identify Compute Resources

- Filter the Aspire application model to collect **deployable compute resources**: `ProjectResource`, `ContainerResource`, and the optional Aspire Dashboard.
- Exclude the `DockerComposeEnvironmentResource` itself, resources annotated as Dokploy-native databases, and resources without a published Compose service mapping.

## 5. Bootstrap Project Container Registry (if needed)

Pipeline step: `dokploy-registry-{name}`.

Only runs when **no explicit container registry** is configured on the environment or on any compute resource.

1. Reuse a matching Dokploy **registry record** when one already exists.
2. Prefer an existing registry compose domain as the effective registry host. If the registry record points at a stale host, defer updating the registry record until the effective host accepts HTTPS registry logins, because Dokploy validates `registry.update` with Docker login.
3. Probe the effective registry host with the registry record credentials. If the live registry rejects them, repair the existing compose service with those credentials and redeploy it.
4. Generate **registry credentials** for the effective host when the registry record does not expose stored credentials (username = project slug, password = HMAC-SHA256 of project ID + host + API key).
5. Create (or reuse) a Dokploy **Compose service** running `registry:2` with htpasswd auth only when no matching registry record exists yet. The Compose file overrides the entrypoint to write `/auth/htpasswd` before the official registry process starts.
6. Compare the existing registry Compose file while ignoring the generated htpasswd hash value. A stable credential fingerprint still detects real credential changes.
7. Deploy the compose service only when it is new, changed, missing a domain, or not accepting the expected credentials.
8. Create a **HTTPS domain** with Let's Encrypt when missing and no existing registry record/domain can be reused.
9. Skip the registry compose deployment when the registry record, compose service, domain, and credentials are already valid.
10. **Wait for the registry** to accept credentials (retry loop with timeout) only after setup or repair work.
11. Register (or update) the registry in Dokploy's registry list so applications can reference it.

## 6. Push Application Images to Registry

Pipeline step: `dokploy-images-{name}`.

This step also depends on the no-op `docker-compose-up-{name}` compatibility bridge so Aspire's generated `build-*` steps that still reference the Docker Compose up step run before images are pushed. The bridge does not execute Docker Compose.

For each compute resource (except the Aspire Dashboard):

1. Resolve the **local Docker/Podman image** (inspect or list by repository name).
2. Tag it with the **project registry** prefix.
3. Log in to the registry (retry loop until credentials are accepted).
4. `docker/podman push` the tagged image.

## 7. Provision Dokploy-Native Databases

Pipeline step: `dokploy-databases-{name}`.

This step depends on the project registry step and can run in parallel with `dokploy-images-{name}`. Application configuration waits for both image push and database provisioning to complete.

For each resource annotated with `DokployDatabaseAnnotation`:

1. Extract **credentials** (user, password, database name, Docker image) from the Aspire resource's parameters. Apply sensible defaults when not explicitly configured.
2. **Sanitize the password** to match Dokploy's allowed character set; pad if too short.
3. Search for an existing database of the same name in the target environment.
   - If found → reconcile configuration (recreate if credentials changed, otherwise update metadata).
   - If not found → create via the appropriate Dokploy API (`postgres.create`, `redis.create`, `mysql.create`, `mariadb.create`, `mongo.create`).
4. **Deploy** the database (trigger container start).
5. **Wait for the database** to become readable — poll the API until connection details (host, port, credentials) can be read back.

Supported database types: **PostgreSQL, Redis, MySQL, MariaDB, MongoDB**.

## 8. Build Internal Hostname & Port Mappings

- Build a **resource → hostname** dictionary: compute resources map to their Dokploy `appName`; database resources map to the host reported by the provisioned database.
- Build an **endpoint port override** dictionary from provisioned database connections (so env var resolution uses actual Dokploy ports).

## 9. Create & Configure Application Shells

Pipeline step: `dokploy-applications-{name}`.

For each compute resource:

### 9a. Ensure Application Shell

- Search for an existing Dokploy application with the same sanitized name in the target environment.
- If found → reuse it. If not → create a new application via the API.
- Record the `appName` as the resource's internal hostname.

### 9b. Configure the Application

1. **Set Docker image source** — resolve the image reference (from project registry or published compose service). Call `saveDockerProvider` on the Dokploy API. If using the project registry, also link the registry ID.
2. **Set command & args** — extract entrypoint/command from the published Compose service.
3. **Resolve environment variables** — execute Aspire's environment callbacks, then structurally resolve each value:
   - `EndpointReference` / `EndpointReferenceExpression` → substitute with Dokploy hostname + port.
   - `ConnectionStringReference` → unwrap and resolve recursively.
   - `ParameterResource` → resolve directly.
   - `ReferenceExpression` → format-string with resolved placeholders.
   - Normalize reverse-proxy cluster addresses and rewrite `ConnectionStrings__*` entries using actual database connection details.
4. **Save environment variables** to the application.
5. **Sync domains** — first reuse any existing Dokploy application domains. When no domain exists and the resource exposes an external HTTP/HTTPS endpoint, derive a public hostname (prefer DNS-resolvable subdomain, fall back to sslip.io), then create the Dokploy domain with HTTPS on the target port.

Configuration writes are skipped when the existing application already has the desired Docker provider, registry link, command/args, environment variables, and domains.

## 10. Trigger Deployments

Pipeline step: `dokploy-release-{name}`.

For each configured application that changed, call `DeployApplication` on the Dokploy API. Unchanged applications whose last deployment status is already successful are skipped so Dokploy does not restart containers unnecessarily.

## 11. Print Deployment Summary

Pipeline step: `dokploy-summary-{name}`.

Collect and display:

| Entry | Example |
|---|---|
| 🚀 Target | Dokploy |
| 🌐 Server | `https://panel.example.com` |
| 🏢 Organization | (if applicable) |
| 📦 Project | `my-app` |
| 🧭 Environment | `production` |
| 🔗 *resource* | `https://frontend-my-app.example.com` |
| 📚 Registry | `container-registry-my-app-a1b2c3d4.1.2.3.4.sslip.io` |
| 🗃️ *database* | `postgres-host:5432/mydb` |

## Destroy Flow

`aspire destroy` uses the same resolved Dokploy target configuration, but it does not create missing Dokploy objects. The integration replaces Docker Compose's `destroy-compose-{name}` pipeline step with named cleanup phases:

The `destroy-compose-{name}` name remains as a no-op compatibility bridge for Aspire's generated destroy graph, then hands off immediately to `dokploy-destroy-validate-{name}`.

| Step | Purpose |
|---|---|
| `dokploy-destroy-validate-{name}` | Resolve and validate Dokploy connection parameters. |
| `dokploy-destroy-discover-{name}` | Find the target project/environment and compute resources. |
| `dokploy-destroy-applications-{name}` | Delete matching Dokploy applications and their managed domains. |
| `dokploy-destroy-databases-{name}` | Delete matching Dokploy-native databases. |
| `dokploy-destroy-registry-{name}` | Delete the auto-bootstrapped registry Compose service and registry record. |
| `dokploy-destroy-project-{name}` | Remove the project only if no services remain. |
| `dokploy-destroy-summary-{name}` | Write the Aspire destroy summary. |

1. Resolve and validate the Dokploy server URL, API key, project name, and environment name.
2. Find the matching project in the active Dokploy organization. If the project does not exist, destroy is a no-op.
3. Find the target environment inside that project. If it does not exist, destroy removes the project only if it is already empty.
4. Delete applications whose names match the Aspire compute resources, removing their managed domains first.
5. Delete Dokploy-native PostgreSQL, Redis, MySQL, MariaDB, and MongoDB resources whose names match the annotated Aspire database resources.
6. If the deployment used the auto-bootstrapped project registry, delete the registry Compose service with volumes and remove the Dokploy registry record.
7. Inspect the project. If no applications, compose services, or native databases remain, remove the project. If unrelated services remain, keep the project and report that in the summary.
