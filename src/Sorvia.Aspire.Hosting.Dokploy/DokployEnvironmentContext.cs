
#pragma warning disable ASPIREPIPELINES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dokploy;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

internal sealed class DokployEnvironmentContext(DokployEnvironmentResource environment, ILogger logger)
{
    internal async Task<DokployServiceResource> CreateServiceResourceAsync(IResource resource, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
    {
        logger.LogInformation("Creating Dokploy service resource for resource '{ResourceName}' in environment '{EnvironmentName}'", resource.Name, environment.Name);
        return new DokployServiceResource($"{resource.Name}", resource, environment);
    }
}
