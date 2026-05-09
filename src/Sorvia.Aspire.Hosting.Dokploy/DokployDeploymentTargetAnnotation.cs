using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker;
using Aspire.Hosting.Docker.Resources;

namespace Aspire.Hosting.Dokploy;

internal sealed class DokployDeploymentTargetAnnotation : IResourceAnnotation
{
    private readonly Dictionary<string, DokployPublishedComposeService> _publishedComposeServices = new(StringComparer.OrdinalIgnoreCase);

    public ParameterResource? ServerUrlParameter { get; set; }

    public ParameterResource? ApiKeyParameter { get; set; }

    public ParameterResource? ProjectNameParameter { get; set; }

    public ParameterResource? DeploymentEnvironmentNameParameter { get; set; }

    public IContainerRegistry? DefaultContainerRegistry { get; set; }

    public string? ServerUrl { get; set; }

    public string? ApiKey { get; set; }

    public string? ProjectName { get; set; }

    public string? DeploymentEnvironmentName { get; set; }

    public bool PipelineConfigured { get; set; }

    public IReadOnlyDictionary<string, DokployPublishedComposeService> PublishedComposeServices => _publishedComposeServices;

    public void CaptureComposeFile(ComposeFile composeFile)
    {
        ArgumentNullException.ThrowIfNull(composeFile);

        _publishedComposeServices.Clear();

        foreach (var (serviceName, service) in composeFile.Services)
        {
            _publishedComposeServices[NormalizeServiceName(serviceName)] = new DokployPublishedComposeService(
                serviceName,
                service.Image,
                service.Entrypoint.ToArray(),
                service.Command.ToArray());
        }
    }

    public bool TryGetPublishedComposeService(IResource resource, out DokployPublishedComposeService service)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return _publishedComposeServices.TryGetValue(NormalizeServiceName(resource.Name), out service!);
    }

    private static string NormalizeServiceName(string name)
        => name.Replace('_', '-').ToLowerInvariant();
}

internal sealed record DokployPublishedComposeService(
    string ServiceName,
    string? Image,
    IReadOnlyList<string> Entrypoint,
    IReadOnlyList<string> Command);
