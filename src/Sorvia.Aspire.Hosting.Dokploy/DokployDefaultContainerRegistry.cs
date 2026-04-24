
#pragma warning disable ASPIREPIPELINES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Represents a default container registry for Dokploy deployments, which uses a registry hosted in dokploy as a docker container.
/// This container will be created automatically by the Dokploy deployment if any resources reference no other container registry.
/// </summary>
internal sealed class DokployDefaultContainerRegistry : IContainerRegistry
{
    public static readonly DokployDefaultContainerRegistry Instance = new();

    private DokployDefaultContainerRegistry() { }

    /// <inheritdoc/>
    public ReferenceExpression Name => ReferenceExpression.Create($"dokploy-default-registry");

    /// <inheritdoc/>
    /// <remarks>
    /// Returns an empty string for local Docker Compose scenarios where images are built and used locally
    /// without being pushed to a remote registry. The empty endpoint indicates no remote registry is involved.
    /// </remarks>
    public ReferenceExpression Endpoint { get;  set; } = ReferenceExpression.Create($"{""}");

    /// <inheritdoc/>
    public ReferenceExpression? Repository { get; set; } = null;
}
