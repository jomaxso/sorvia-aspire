# Dokploy Deployment Flow

High-level overview of the `DokployDeploymentExecutor.DeployToDokployAsync` pipeline.

---

## 1. Validate Configuration

- Resolve **server URL** and **API key** from parameters (or use defaults).
- Fail early if either value is missing or blank.
- Normalize the server URL (ensure valid URI with scheme).
- Perform a connectivity check by calling `ListProjects` on the Dokploy API — if the server is unreachable, abort with a descriptive error.

## 2. Resolve Deployment Parameters

- Resolve the **project name** from the environment resource parameter.
- Resolve the **deployment environment name** (e.g. `production`); normalize to lowercase, default to `"production"` when empty.
- Optionally resolve the **active organization** (used to scope project lookup).

## 3. Find or Create Dokploy Project & Environment

- Query existing projects by name. If an organization is active, match within that organization.
- If no matching project exists → create a new one via the API.
- Within the project, find the target environment by name. If it doesn't exist → create it.

## 4. Identify Compute Resources

- Filter the Aspire application model to collect **deployable compute resources**: `ProjectResource`, `ContainerResource`, and the optional Aspire Dashboard.
- Exclude the `DokployEnvironmentResource` itself, resources annotated as Dokploy-native databases, and resources without a published Compose service mapping.

## 5. Bootstrap Project Container Registry (if needed)

Only runs when **no explicit container registry** is configured on the environment or on any compute resource.

1. Derive a **sslip.io registry hostname** from the Dokploy server's IP address.
2. Generate **registry credentials** (username = project slug, password = HMAC-SHA256 of project ID + host + API key).
3. Create (or reuse) a Dokploy **Compose service** running `registry:2` with htpasswd auth.
4. Deploy the compose service and create a **HTTPS domain** with Let's Encrypt.
5. **Wait for the registry** to accept credentials (retry loop with timeout).
6. Register (or update) the registry in Dokploy's registry list so applications can reference it.

## 6. Push Application Images to Registry

For each compute resource (except the Aspire Dashboard):

1. Resolve the **local Docker/Podman image** (inspect or list by repository name).
2. Tag it with the **project registry** prefix.
3. Log in to the registry (retry loop until credentials are accepted).
4. `docker/podman push` the tagged image.

## 7. Provision Dokploy-Native Databases

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
5. **Sync domains** — derive a public hostname (prefer DNS-resolvable subdomain, fall back to sslip.io), then create or update the Dokploy domain with HTTPS on the target port. Remove domains for resources without external endpoints.

## 10. Trigger Deployments

For each configured application, call `DeployApplication` on the Dokploy API. This tells Dokploy to pull the Docker image and start/restart the container.

## 11. Print Deployment Summary

Collect and display:

| Entry | Example |
|---|---|
| 🚀 Target | Dokploy |
| 🌐 Server | `https://panel.example.com` |
| 🏢 Organization | (if applicable) |
| 📦 Project | `my-app` |
| 🧭 Environment | `production` |
| 🔗 *resource* | `https://frontend-my-app.example.com` |
| 📚 Registry | `container-registry-my-app.1.2.3.4.sslip.io` |
| 🗃️ *database* | `postgres-host:5432/mydb` |
