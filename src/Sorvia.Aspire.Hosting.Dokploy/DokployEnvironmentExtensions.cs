#pragma warning disable ASPIREINTERACTION001 // This type is used for interaction with the Dokploy REST API and is not intended for direct use by application code. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREATS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREPIPELINES001 // Custom deployment target replaces the stock Docker Compose deploy step.

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
///   <item><description><c>docker-compose-up-{name}</c> — Validates Dokploy configuration and deploys resources to Dokploy. DependsOn <c>prepare-{name}</c>, RequiredBy <c>Deploy</c>.</description></item>
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
                steps.RemoveAll(step => string.Equals(step.Name, $"docker-compose-up-{resource.Name}", StringComparison.Ordinal));
                steps.RemoveAll(IsDockerComposePrintSummaryStep);

                steps.Add(new PipelineStep
                {
                    Name = $"docker-compose-up-{resource.Name}",
                    Description = $"Deploy resources for environment {resource.Name} using Dokploy",
                    Tags = ["docker-compose-up", "dokploy", "dokploy-deploy"],
                    Resource = resource,
                    Action = ctx => DokployDeploymentExecutor.DeployToDokployAsync(ctx, resource, target),
                    DependsOnSteps = [$"prepare-{resource.Name}"],
                    RequiredBySteps = [WellKnownPipelineSteps.Deploy],
                });

                return steps;
            });

            resource.Annotations.Remove(annotation);
            resource.Annotations.Add(wrapper);
        }

        target.PipelineConfigured = true;
    }

    private static bool IsDockerComposePrintSummaryStep(PipelineStep step)
        => step.Tags.Any(tag => string.Equals(tag, "print-summary", StringComparison.OrdinalIgnoreCase));
}
