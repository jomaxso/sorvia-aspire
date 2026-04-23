using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Dokploy;

#pragma warning disable ASPIREATS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

[AspireExport(ExposeProperties = true)]
public class DokployAspireDashboardResource(string name) : ContainerResource(name)
{
    /// <summary>
    /// Gets the primary endpoint of the Aspire Dashboard.
    /// </summary>
    public EndpointReference PrimaryEndpoint => new(this, "http");

    /// <summary>
    /// Gets the OTLP gRPC endpoint for telemetry data.
    /// </summary>
    public EndpointReference OtlpGrpcEndpoint => new(this, "otlp-grpc");
}
