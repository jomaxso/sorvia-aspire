#pragma warning disable ASPIREINTERACTION001 // This type is used for interaction with the Dokploy REST API and is not intended for direct use by application code. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREATS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREPIPELINES001 // Custom deployment target replaces the stock Docker Compose deploy/destroy steps.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker;
using Aspire.Hosting.Dokploy;
using Aspire.Hosting.Lifecycle;
using Aspire.Hosting.Pipelines;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for adding Dokploy deployment support to an Aspire distributed application.
/// </summary>
/// <remarks>
/// Dokploy (https://dokploy.com) is a free, self-hostable PaaS that simplifies deployment
/// and management of applications and databases. This integration enables deploying an entire
/// Aspire application to a Dokploy instance with a single method call.
///
/// <para><b>How it works:</b></para>
/// <list type="number">
///   <item><description>
///     <see cref="AddDokployEnvironment"/> creates a Docker Compose publishing environment
///     and configures its deployment target for Dokploy.
///   </description></item>
///   <item><description>
///     When the AppHost runs in publish mode, the resource reuses Aspire.Hosting.Docker's
///     Docker Compose publish and prepare behavior.
///   </description></item>
///   <item><description>
///     The deploy step validates Dokploy configuration, provisions Dokploy-native databases,
///     and deploys application resources to Dokploy via the REST API.
///   </description></item>
/// </list>
///
/// <para><b>Pipeline steps:</b></para>
/// <para>
/// The resource follows the Docker Compose pipeline shape but swaps the deploy behavior:
/// </para>
/// <list type="bullet">
///   <item><description><c>publish-{name}</c> — Runs the exact Aspire.Hosting.Docker publish implementation. RequiredBy <c>Publish</c>.</description></item>
///   <item><description><c>prepare-{name}</c> — Runs the exact Aspire.Hosting.Docker prepare implementation before deployment.</description></item>
///   <item><description><c>dokploy-validate-{name}</c> through <c>dokploy-summary-{name}</c> — Validates configuration, reconciles project state, handles registry/images/databases/applications, releases changed applications, and writes the Dokploy summary. The final summary step is RequiredBy <c>Deploy</c>.</description></item>
///   <item><description><c>dokploy-destroy-validate-{name}</c> through <c>dokploy-destroy-summary-{name}</c> — Resolves the destroy target, deletes Dokploy applications, native databases, auto-registry resources, removes the empty project shell, and writes the destroy summary. The final summary step is RequiredBy <c>Destroy</c>.</description></item>
/// </list>
///
/// <para><b>Configuration:</b></para>
/// <para>
/// The Dokploy server URL, API key, project name, and deployment environment are captured
/// as Aspire parameters when <c>aspire deploy</c> needs them. Plain <c>aspire publish</c>
/// can still generate Docker Compose artifacts without Dokploy credentials.
/// </para>
/// </remarks>
public static class DokployEnvironmentExtensions
{
    /// <summary>
    /// Adds a Docker Compose publishing environment whose deploy step targets Dokploy.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">A logical name for the deployment environment.</param>
    /// <returns>The Docker Compose environment builder configured for Dokploy deployment.</returns>
    [AspireExport("addDokployEnvironment", Description = "Adds a Dokploy publishing environment")]
    public static IResourceBuilder<DockerComposeEnvironmentResource> AddDokployEnvironment(
        this IDistributedApplicationBuilder builder,
        string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        return builder.AddDockerComposeEnvironment(name)
            .WithDokployDeploymentTarget();
    }

    /// <summary>
    /// Configures an existing Docker Compose publishing environment to deploy to Dokploy.
    /// </summary>
    /// <param name="environment">The Docker Compose environment builder.</param>
    /// <returns>The same builder for chaining.</returns>
    [AspireExport("withDokployDeploymentTarget", Description = "Deploys a Docker Compose publishing environment to Dokploy")]
    public static IResourceBuilder<DockerComposeEnvironmentResource> WithDokployDeploymentTarget(
        this IResourceBuilder<DockerComposeEnvironmentResource> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        if (environment.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            return environment;
        }

        var target = GetOrCreateDokployTarget(environment.Resource);
        EnsureDokployParameters(environment.ApplicationBuilder, environment.Resource.Name, target);

        environment.ConfigureComposeFile(target.CaptureComposeFile);
        ConfigureDokployPipeline(environment, target);

        return environment;
    }

    private static DokployDeploymentTargetAnnotation GetOrCreateDokployTarget(DockerComposeEnvironmentResource resource)
    {
        if (resource.TryGetLastAnnotation<DokployDeploymentTargetAnnotation>(out var annotation))
        {
            return annotation;
        }

        annotation = new DokployDeploymentTargetAnnotation();
        resource.Annotations.Add(annotation);
        return annotation;
    }

    private static void EnsureDokployParameters(
        IDistributedApplicationBuilder builder,
        string defaultProjectName,
        DokployDeploymentTargetAnnotation target)
    {
        target.ServerUrlParameter ??= builder.AddParameter("dokploy-url")
            .WithDescription("URL of the Dokploy server to deploy to.")
            .Resource;

        target.ApiKeyParameter ??= builder.AddParameter("dokploy-api-key", secret: true)
            .WithDescription("API key for authenticating with the Dokploy server.")
            .Resource;

        target.ProjectNameParameter ??= builder.AddParameter("dokploy-project-name")
            .WithDescription("Target Dokploy project name. Leave empty to use the environment name.")
            .WithCustomInput(parameter => new()
            {
                Name = parameter.Name,
                Label = "Dokploy project name",
                Description = parameter.Description,
                InputType = InputType.Text,
                Placeholder = defaultProjectName,
                Value = defaultProjectName,
                Required = true
            })
            .Resource;

        target.DeploymentEnvironmentNameParameter ??= builder.AddParameter("dokploy-environment")
            .WithDescription("Target Dokploy environment inside the project. Leave empty to use production.")
            .WithCustomInput(parameter => new()
            {
                Name = parameter.Name,
                Label = "Dokploy environment",
                Description = parameter.Description,
                InputType = InputType.Text,
                Placeholder = "production",
                Value = "production",
                Required = false
            })
            .Resource;
    }

    private static void ConfigureDokployPipeline(
        IResourceBuilder<DockerComposeEnvironmentResource> environment,
        DokployDeploymentTargetAnnotation target)
    {
        if (target.PipelineConfigured)
        {
            return;
        }

        var resource = environment.Resource;
        var stepAnnotations = resource.Annotations
            .OfType<PipelineStepAnnotation>()
            .ToArray();

        foreach (var annotation in stepAnnotations)
        {
            var wrapper = new PipelineStepAnnotation(async factoryContext =>
            {
                var steps = new List<PipelineStep>(await annotation.CreateStepsAsync(factoryContext).ConfigureAwait(false));
                var dockerComposeUpStepName = $"docker-compose-up-{resource.Name}";
                var dockerComposeDestroyStepName = $"destroy-compose-{resource.Name}";
                var dokployValidateStepName = $"dokploy-validate-{resource.Name}";
                var dokployProjectStepName = $"dokploy-project-{resource.Name}";
                var dokployRegistryStepName = $"dokploy-registry-{resource.Name}";
                var dokployImagesStepName = $"dokploy-images-{resource.Name}";
                var dokployDatabasesStepName = $"dokploy-databases-{resource.Name}";
                var dokployApplicationsStepName = $"dokploy-applications-{resource.Name}";
                var dokployReleaseStepName = $"dokploy-release-{resource.Name}";
                var dokploySummaryStepName = $"dokploy-summary-{resource.Name}";
                var dokployDestroyValidateStepName = $"dokploy-destroy-validate-{resource.Name}";
                var dokployDestroyDiscoverStepName = $"dokploy-destroy-discover-{resource.Name}";
                var dokployDestroyApplicationsStepName = $"dokploy-destroy-applications-{resource.Name}";
                var dokployDestroyDatabasesStepName = $"dokploy-destroy-databases-{resource.Name}";
                var dokployDestroyRegistryStepName = $"dokploy-destroy-registry-{resource.Name}";
                var dokployDestroyProjectStepName = $"dokploy-destroy-project-{resource.Name}";
                var dokployDestroySummaryStepName = $"dokploy-destroy-summary-{resource.Name}";

                foreach (var step in steps)
                {
                    ReplaceStepReference(step.RequiredBySteps, dockerComposeUpStepName, dokployImagesStepName);
                    ReplaceStepReference(step.DependsOnSteps, dockerComposeUpStepName, dokploySummaryStepName);
                    ReplaceStepReference(step.RequiredBySteps, dockerComposeDestroyStepName, dokployDestroyValidateStepName);
                    ReplaceStepReference(step.DependsOnSteps, dockerComposeDestroyStepName, dokployDestroySummaryStepName);
                }

                steps.RemoveAll(step => string.Equals(step.Name, dockerComposeUpStepName, StringComparison.Ordinal));
                steps.RemoveAll(step => string.Equals(step.Name, dockerComposeDestroyStepName, StringComparison.Ordinal));
                steps.RemoveAll(step => IsDokployStepForResource(step, resource));
                steps.RemoveAll(IsDockerComposePrintSummaryStep);

                steps.Add(CreateDokployStep(
                    resource,
                    dockerComposeUpStepName,
                    $"Resolve Docker Compose build prerequisites for Dokploy environment {resource.Name}",
                    _ => Task.CompletedTask,
                    [$"prepare-{resource.Name}"],
                    tags: ["dokploy-compat"]));
                steps.Add(CreateDokployStep(
                    resource,
                    dockerComposeDestroyStepName,
                    $"Resolve Docker Compose destroy prerequisites for Dokploy environment {resource.Name}",
                    _ => Task.CompletedTask,
                    [WellKnownPipelineSteps.DestroyPrereq],
                    tags: ["dokploy-compat"]));

                steps.Add(CreateDokployStep(
                    resource,
                    dokployValidateStepName,
                    $"Validate Dokploy configuration for environment {resource.Name}",
                    ctx => DokployDeploymentExecutor.ValidateDokployDeploymentAsync(ctx, resource, target),
                    [$"prepare-{resource.Name}"],
                    tags: ["dokploy-deploy", "dokploy-validate"]));
                steps.Add(CreateDokployStep(
                    resource,
                    dokployProjectStepName,
                    $"Reconcile Dokploy project and environment for {resource.Name}",
                    ctx => DokployDeploymentExecutor.ReconcileDokployProjectAsync(ctx, resource, target),
                    [dokployValidateStepName],
                    tags: ["dokploy-deploy", "dokploy-project"]));
                steps.Add(CreateDokployStep(
                    resource,
                    dokployRegistryStepName,
                    $"Ensure Dokploy project registry for {resource.Name}",
                    ctx => DokployDeploymentExecutor.EnsureDokployRegistryAsync(ctx, resource, target),
                    [dokployProjectStepName],
                    tags: ["dokploy-deploy", "dokploy-registry"]));
                steps.Add(CreateDokployStep(
                    resource,
                    dokployImagesStepName,
                    $"Push application images for {resource.Name}",
                    ctx => DokployDeploymentExecutor.PushDokployImagesAsync(ctx, resource, target),
                    [dokployRegistryStepName, dockerComposeUpStepName],
                    tags: ["dokploy-deploy", "dokploy-images"]));
                steps.Add(CreateDokployStep(
                    resource,
                    dokployDatabasesStepName,
                    $"Provision Dokploy databases for {resource.Name}",
                    ctx => DokployDeploymentExecutor.ProvisionDokployDatabasesAsync(ctx, resource, target),
                    [dokployImagesStepName],
                    tags: ["dokploy-deploy", "dokploy-databases"]));
                steps.Add(CreateDokployStep(
                    resource,
                    dokployApplicationsStepName,
                    $"Configure Dokploy applications for {resource.Name}",
                    ctx => DokployDeploymentExecutor.ConfigureDokployApplicationsAsync(ctx, resource, target),
                    [dokployDatabasesStepName],
                    tags: ["dokploy-deploy", "dokploy-applications"]));
                steps.Add(CreateDokployStep(
                    resource,
                    dokployReleaseStepName,
                    $"Release changed Dokploy applications for {resource.Name}",
                    ctx => DokployDeploymentExecutor.ReleaseDokployApplicationsAsync(ctx, resource, target),
                    [dokployApplicationsStepName],
                    tags: ["dokploy-deploy", "dokploy-release"]));
                steps.Add(CreateDokployStep(
                    resource,
                    dokploySummaryStepName,
                    $"Write Dokploy deployment summary for {resource.Name}",
                    ctx => DokployDeploymentExecutor.WriteDokployDeploymentSummaryAsync(ctx, resource, target),
                    [dokployReleaseStepName],
                    [WellKnownPipelineSteps.Deploy],
                    ["dokploy-deploy", "dokploy-summary"]));

                steps.Add(CreateDokployStep(
                    resource,
                    dokployDestroyValidateStepName,
                    $"Validate Dokploy destroy configuration for {resource.Name}",
                    ctx => DokployDeploymentExecutor.ValidateDokployDestroyAsync(ctx, resource, target),
                    [dockerComposeDestroyStepName],
                    tags: ["dokploy-destroy", "dokploy-destroy-validate"]));
                steps.Add(CreateDokployStep(
                    resource,
                    dokployDestroyDiscoverStepName,
                    $"Discover Dokploy destroy target for {resource.Name}",
                    ctx => DokployDeploymentExecutor.DiscoverDokployDestroyTargetAsync(ctx, resource, target),
                    [dokployDestroyValidateStepName],
                    tags: ["dokploy-destroy", "dokploy-destroy-discover"]));
                steps.Add(CreateDokployStep(
                    resource,
                    dokployDestroyApplicationsStepName,
                    $"Destroy Dokploy applications for {resource.Name}",
                    ctx => DokployDeploymentExecutor.DestroyDokployApplicationsAsync(ctx, resource, target),
                    [dokployDestroyDiscoverStepName],
                    tags: ["dokploy-destroy", "dokploy-destroy-applications"]));
                steps.Add(CreateDokployStep(
                    resource,
                    dokployDestroyDatabasesStepName,
                    $"Destroy Dokploy databases for {resource.Name}",
                    ctx => DokployDeploymentExecutor.DestroyDokployDatabasesAsync(ctx, resource, target),
                    [dokployDestroyApplicationsStepName],
                    tags: ["dokploy-destroy", "dokploy-destroy-databases"]));
                steps.Add(CreateDokployStep(
                    resource,
                    dokployDestroyRegistryStepName,
                    $"Destroy Dokploy project registry for {resource.Name}",
                    ctx => DokployDeploymentExecutor.DestroyDokployRegistryAsync(ctx, resource, target),
                    [dokployDestroyDatabasesStepName],
                    tags: ["dokploy-destroy", "dokploy-destroy-registry"]));
                steps.Add(CreateDokployStep(
                    resource,
                    dokployDestroyProjectStepName,
                    $"Remove empty Dokploy project for {resource.Name}",
                    ctx => DokployDeploymentExecutor.RemoveEmptyDokployProjectAsync(ctx, resource, target),
                    [dokployDestroyRegistryStepName],
                    tags: ["dokploy-destroy", "dokploy-destroy-project"]));
                steps.Add(CreateDokployStep(
                    resource,
                    dokployDestroySummaryStepName,
                    $"Write Dokploy destroy summary for {resource.Name}",
                    ctx => DokployDeploymentExecutor.WriteDokployDestroySummaryAsync(ctx, resource, target),
                    [dokployDestroyProjectStepName],
                    [WellKnownPipelineSteps.Destroy],
                    ["dokploy-destroy", "dokploy-destroy-summary"]));

                return steps;
            });

            resource.Annotations.Remove(annotation);
            resource.Annotations.Add(wrapper);
        }

        target.PipelineConfigured = true;
    }

    private static bool IsDockerComposePrintSummaryStep(PipelineStep step)
        => step.Tags.Any(tag => string.Equals(tag, "print-summary", StringComparison.OrdinalIgnoreCase));

    private static bool IsDokployStepForResource(PipelineStep step, DockerComposeEnvironmentResource resource)
        => ReferenceEquals(step.Resource, resource)
           && step.Tags.Any(tag => string.Equals(tag, "dokploy", StringComparison.OrdinalIgnoreCase));

    private static void ReplaceStepReference(List<string> stepNames, string oldStepName, string newStepName)
    {
        for (var i = 0; i < stepNames.Count; i++)
        {
            if (string.Equals(stepNames[i], oldStepName, StringComparison.Ordinal))
            {
                stepNames[i] = newStepName;
            }
        }
    }

    private static PipelineStep CreateDokployStep(
        DockerComposeEnvironmentResource resource,
        string name,
        string description,
        Func<PipelineStepContext, Task> action,
        string[] dependsOnSteps,
        string[]? requiredBySteps = null,
        string[]? tags = null)
        => new()
        {
            Name = name,
            Description = description,
            Tags = tags is null ? ["dokploy"] : ["dokploy", .. tags],
            Resource = resource,
            Action = action,
            DependsOnSteps = new List<string>(dependsOnSteps),
            RequiredBySteps = requiredBySteps is null ? [] : new List<string>(requiredBySteps),
        };
}
