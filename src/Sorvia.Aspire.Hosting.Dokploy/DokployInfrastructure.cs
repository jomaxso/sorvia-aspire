
#pragma warning disable ASPIREPIPELINES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dokploy;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

internal sealed class DokployInfrastructure(
    ILogger<DokployInfrastructure> logger,
    DistributedApplicationExecutionContext executionContext) : IDistributedApplicationEventingSubscriber
{
    public Task SubscribeAsync(IDistributedApplicationEventing eventing, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
    {
        eventing.Subscribe<BeforeStartEvent>(OnBeforeStartAsync);
        return Task.CompletedTask;
    }

    private async Task OnBeforeStartAsync(BeforeStartEvent @event, CancellationToken cancellationToken)
    {
        if (executionContext.IsRunMode)
        {
            return;
        }

        // Find Dokploy environment resources
        var dokployEnvironments = @event.Model.Resources
            .OfType<DokployEnvironmentResource>()
            .ToArray();

        if (dokployEnvironments.Length == 0)
        {
            return;
        }

        foreach (var environment in dokployEnvironments)
        {
            var dokployEnvironmentContext = new DokployEnvironmentContext(environment, logger);

            if (environment.DashboardEnabled && environment.Dashboard?.Resource is DokployAspireDashboardResource dashboard)
            {
                // Ensure the dashboard resource is created (even though it's not part of the main application model)
                var dashboardService = await dokployEnvironmentContext.CreateServiceResourceAsync(dashboard, executionContext, cancellationToken).ConfigureAwait(false);

                dashboard.Annotations.Add(new DeploymentTargetAnnotation(dashboardService)
                {
                    ComputeEnvironment = environment,
                    ContainerRegistry = GetContainerRegistry(environment, @event.Model)
                });
            }

            foreach (var r in @event.Model.GetComputeResources())
            {
                // Skip resources that are explicitly targeted to a different compute environment
                var resourceComputeEnvironment = r.GetComputeEnvironment();
                if (resourceComputeEnvironment is not null && resourceComputeEnvironment != environment)
                {
                    continue;
                }

                // Configure OTLP for resources if dashboard is enabled (before creating the service resource)
                if (environment.DashboardEnabled && environment.Dashboard?.Resource.OtlpGrpcEndpoint is EndpointReference otlpGrpcEndpoint)
                {
                    ConfigureOtlp(r, otlpGrpcEndpoint);
                }

                // Create a Docker Compose compute resource for the resource
                var serviceResource = await dokployEnvironmentContext.CreateServiceResourceAsync(r, executionContext, cancellationToken).ConfigureAwait(false);

                // Add deployment target annotation to the resource
                r.Annotations.Add(new DeploymentTargetAnnotation(serviceResource)
                {
                    ComputeEnvironment = environment, 
                    ContainerRegistry = GetContainerRegistry(environment, @event.Model)
                });
            }
        }
    }

    private static IContainerRegistry GetContainerRegistry(DokployEnvironmentResource environment, DistributedApplicationModel appModel)
    {
        // Check for explicit container registry reference annotation on the environment
        if (environment.TryGetLastAnnotation<ContainerRegistryReferenceAnnotation>(out var annotation))
        {
            return annotation.Registry;
        }

        // Check if there's a single container registry in the app model
        var registries = appModel.Resources.OfType<IContainerRegistry>().ToArray();
        if (registries.Length == 1)
        {
            return registries[0];
        }

        // Fall back to local container registry for Dokploy scenarios
        // string? defaultRegistryEndpoint = environment.DefaultRegistryEndpoint ??
        //     throw new InvalidOperationException("No container registry reference found for Dokploy environment, and no default registry endpoint configured. Please configure a default registry endpoint by setting the DOKPLOY_DEFAULT_REGISTRY_ENDPOINT environment variable, or add a container registry resource and reference it from the Dokploy environment.");
    

        return DokployDefaultContainerRegistry.Instance;
    }

    private static void ConfigureOtlp(IResource resource, EndpointReference otlpEndpoint)
    {
        // Only configure OTLP for resources that have the OtlpExporterAnnotation and implement IResourceWithEnvironment
        if (resource is IResourceWithEnvironment resourceWithEnv && resource.Annotations.OfType<OtlpExporterAnnotation>().Any())
        {
            // Configure OTLP environment variables
            resourceWithEnv.Annotations.Add(new EnvironmentCallbackAnnotation(context =>
            {
                context.EnvironmentVariables["OTEL_EXPORTER_OTLP_ENDPOINT"] = otlpEndpoint;
                context.EnvironmentVariables["OTEL_EXPORTER_OTLP_PROTOCOL"] = "grpc";
                context.EnvironmentVariables["OTEL_SERVICE_NAME"] = resource.Name;
                return Task.CompletedTask;
            }));
        }
    }
}
