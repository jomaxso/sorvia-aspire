// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Dokploy.Annotations;

/// <summary>
/// Annotation that marks a database resource for Dokploy-native provisioning.
/// When this annotation is present, the resource is provisioned via the Dokploy REST API
/// (e.g., <c>postgres.create</c>, <c>redis.create</c>) instead of being included in the
/// Docker Compose file as a container service.
/// </summary>
/// <remarks>
/// <para>
/// Database credentials (username, password, database name, Docker image) are extracted
/// from the Aspire resource's parameters at provisioning time and passed to the Dokploy API.
/// This ensures that customizations like <c>.WithUserName()</c>, <c>.WithPassword()</c>,
/// or custom Docker images are respected when provisioning native Dokploy databases.
/// </para>
/// <para>
/// The supported Dokploy API fields per database type:
/// </para>
/// <list type="bullet">
///   <item><description><b>PostgreSQL:</b> name, databaseName, databaseUser, databasePassword, dockerImage</description></item>
///   <item><description><b>Redis:</b> name, databasePassword, dockerImage</description></item>
///   <item><description><b>MySQL:</b> name, databaseName, databaseUser, databasePassword, databaseRootPassword, dockerImage</description></item>
///   <item><description><b>MariaDB:</b> name, databaseName, databaseUser, databasePassword, databaseRootPassword, dockerImage</description></item>
///   <item><description><b>MongoDB:</b> name, databaseUser, databasePassword, dockerImage</description></item>
/// </list>
/// </remarks>
/// <param name="databaseType">The type of Dokploy native database to provision.</param>
public sealed class DokployDatabaseAnnotation(DokployDatabaseType databaseType) : IResourceAnnotation
{
    /// <summary>
    /// The Dokploy-native database type for this resource.
    /// </summary>
    public DokployDatabaseType DatabaseType { get; } = databaseType;

    /// <summary>
    /// The Dokploy database ID, set after the resource is provisioned on the server.
    /// Used for subsequent API calls.
    /// </summary>
    public string? DokployDatabaseId { get; set; }
}

/// <summary>
/// The Dokploy-native database types supported for direct provisioning.
/// </summary>
public enum DokployDatabaseType
{
    /// <summary>PostgreSQL database.</summary>
    Postgres,

    /// <summary>Redis cache/database.</summary>
    Redis,

    /// <summary>MySQL database.</summary>
    MySql,

    /// <summary>MariaDB database.</summary>
    MariaDB,

    /// <summary>MongoDB database.</summary>
    MongoDB
}

/// <summary>
/// Annotation that marks a database resource as connecting to an existing
/// Dokploy-provisioned database, rather than creating a new one.
/// </summary>
public sealed class DokployExistingDatabaseAnnotation : IResourceAnnotation
{
    /// <summary>
    /// The connection string to the existing Dokploy database.
    /// </summary>
    public string ConnectionString { get; }

    /// <summary>
    /// The parameter resource providing the connection string, if one was used.
    /// When set, takes precedence over the <see cref="ConnectionString"/> string value.
    /// </summary>
    public ParameterResource? ConnectionStringParameter { get; }

    /// <summary>
    /// Creates an annotation with a hard-coded connection string.
    /// </summary>
    /// <param name="connectionString">The connection string to the existing database.</param>
    public DokployExistingDatabaseAnnotation(string connectionString)
    {
        ConnectionString = connectionString;
    }

    /// <summary>
    /// Creates an annotation with a connection string provided by an Aspire parameter resource.
    /// The parameter value is resolved at pipeline execution time.
    /// </summary>
    /// <param name="connectionStringParameter">The parameter resource providing the connection string.</param>
    public DokployExistingDatabaseAnnotation(ParameterResource connectionStringParameter)
    {
        ConnectionString = string.Empty;
        ConnectionStringParameter = connectionStringParameter;
    }
}
