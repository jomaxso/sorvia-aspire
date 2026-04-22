using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dokploy.Annotations;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for adding Dokploy-native database resources to an Aspire application.
/// </summary>
/// <remarks>
/// <para>
/// Dokploy provides built-in support for PostgreSQL, Redis, MySQL, MariaDB, and MongoDB databases.
/// These extension methods create Aspire database resources that:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>Run mode:</b> Run as local Docker containers using the standard Aspire database hosting packages
///     (<c>Aspire.Hosting.PostgreSQL</c>, <c>Aspire.Hosting.Redis</c>, etc.).
///   </description></item>
///   <item><description>
///     <b>Publish mode:</b> Are provisioned as Dokploy-native databases via the Dokploy REST API,
///     with connection strings automatically resolved.
///   </description></item>
/// </list>
/// <para>
/// This follows the same pattern as <c>Aspire.Hosting.Azure.PostgreSQL</c> where
/// <c>AddAzurePostgresFlexibleServer</c> provisions an Azure-native database in publish mode
/// and lifecycle methods control run/publish behavior.
/// </para>
/// </remarks>
public static class DokployDatabaseExtensions
{
    // ── PostgreSQL ──────────────────────────────────────────────────────

    /// <summary>
    /// Adds a PostgreSQL server resource that uses Dokploy's native PostgreSQL provisioning in publish mode
    /// and runs as a local container in run mode.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The name of the PostgreSQL resource.</param>
    /// <param name="port">Optional host port for the local container in run mode.</param>
    /// <returns>
    /// An <see cref="IResourceBuilder{PostgresServerResource}"/> that can be used with
    /// <c>.AddDatabase()</c>, <c>.WithDataVolume()</c>, etc.
    /// </returns>
    /// <example>
    /// <code>
    /// var postgres = builder.AddDokployPostgres("postgres")
    ///     .WithDataVolume();
    /// var db = postgres.AddDatabase("mydb");
    /// </code>
    /// </example>
    public static IResourceBuilder<PostgresServerResource> AddDokployPostgres(
        this IDistributedApplicationBuilder builder,
        string name,
        int? port = null)
    {
        var postgres = builder.AddPostgres(name, port: port);

        if (builder.ExecutionContext.IsPublishMode)
        {
            postgres.Resource.Annotations.Add(
                new DokployDatabaseAnnotation(DokployDatabaseType.Postgres));
        }

        return postgres;
    }

    // ── Redis ───────────────────────────────────────────────────────────

    /// <summary>
    /// Adds a Redis resource that uses Dokploy's native Redis provisioning in publish mode
    /// and runs as a local container in run mode.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The name of the Redis resource.</param>
    /// <param name="port">Optional host port for the local container in run mode.</param>
    /// <returns>An <see cref="IResourceBuilder{RedisResource}"/> for further configuration.</returns>
    /// <example>
    /// <code>
    /// var redis = builder.AddDokployRedis("redis");
    /// builder.AddProject&lt;MyApi&gt;("api").WithReference(redis);
    /// </code>
    /// </example>
    public static IResourceBuilder<RedisResource> AddDokployRedis(
        this IDistributedApplicationBuilder builder,
        string name,
        int? port = null)
    {
        var redis = builder.AddRedis(name, port: port);

        if (builder.ExecutionContext.IsPublishMode)
        {
            redis.Resource.Annotations.Add(
                new DokployDatabaseAnnotation(DokployDatabaseType.Redis));
        }

        return redis;
    }

    // ── MySQL ───────────────────────────────────────────────────────────

    /// <summary>
    /// Adds a MySQL server resource that uses Dokploy's native MySQL provisioning in publish mode
    /// and runs as a local container in run mode.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The name of the MySQL resource.</param>
    /// <param name="port">Optional host port for the local container in run mode.</param>
    /// <returns>
    /// An <see cref="IResourceBuilder{MySqlServerResource}"/> that can be used with
    /// <c>.AddDatabase()</c>, <c>.WithDataVolume()</c>, etc.
    /// </returns>
    public static IResourceBuilder<MySqlServerResource> AddDokployMySql(
        this IDistributedApplicationBuilder builder,
        string name,
        int? port = null)
    {
        var mysql = builder.AddMySql(name, port: port);

        if (builder.ExecutionContext.IsPublishMode)
        {
            mysql.Resource.Annotations.Add(
                new DokployDatabaseAnnotation(DokployDatabaseType.MySql));
        }

        return mysql;
    }

    // ── MariaDB ─────────────────────────────────────────────────────────

    /// <summary>
    /// Adds a MariaDB server resource that uses Dokploy's native MariaDB provisioning in publish mode
    /// and runs as a local MySQL container in run mode (MariaDB is wire-compatible with MySQL).
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The name of the MariaDB resource.</param>
    /// <param name="port">Optional host port for the local container in run mode.</param>
    /// <returns>
    /// An <see cref="IResourceBuilder{MySqlServerResource}"/> that can be used with
    /// <c>.AddDatabase()</c>, <c>.WithDataVolume()</c>, etc.
    /// </returns>
    /// <remarks>
    /// MariaDB uses the MySQL Aspire hosting package since they are wire-compatible.
    /// In publish mode, Dokploy provisions a native MariaDB instance.
    /// </remarks>
    public static IResourceBuilder<MySqlServerResource> AddDokployMariaDB(
        this IDistributedApplicationBuilder builder,
        string name,
        int? port = null)
    {
        var mysql = builder.AddMySql(name, port: port);

        if (builder.ExecutionContext.IsPublishMode)
        {
            mysql.Resource.Annotations.Add(
                new DokployDatabaseAnnotation(DokployDatabaseType.MariaDB));
        }

        return mysql;
    }

    // ── MongoDB ─────────────────────────────────────────────────────────

    /// <summary>
    /// Adds a MongoDB server resource that uses Dokploy's native MongoDB provisioning in publish mode
    /// and runs as a local container in run mode.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The name of the MongoDB resource.</param>
    /// <param name="port">Optional host port for the local container in run mode.</param>
    /// <returns>
    /// An <see cref="IResourceBuilder{MongoDBServerResource}"/> that can be used with
    /// <c>.AddDatabase()</c>, <c>.WithDataVolume()</c>, etc.
    /// </returns>
    public static IResourceBuilder<MongoDBServerResource> AddDokployMongoDB(
        this IDistributedApplicationBuilder builder,
        string name,
        int? port = null)
    {
        var mongo = builder.AddMongoDB(name, port: port);

        if (builder.ExecutionContext.IsPublishMode)
        {
            mongo.Resource.Annotations.Add(
                new DokployDatabaseAnnotation(DokployDatabaseType.MongoDB));
        }

        return mongo;
    }

    // ── PublishAsDokployDatabase ────────────────────────────────────────

    /// <summary>
    /// Configures a standard PostgreSQL resource to be provisioned as a Dokploy-native
    /// database in publish mode. In run mode, it continues to run as a local container.
    /// </summary>
    /// <param name="builder">The PostgreSQL resource builder (from <c>AddPostgres()</c>).</param>
    /// <returns>The resource builder for chaining.</returns>
    /// <example>
    /// <code>
    /// var postgres = builder.AddPostgres("postgres").PublishAsDokployDatabase();
    /// var db = postgres.AddDatabase("mydb");
    /// </code>
    /// </example>
    public static IResourceBuilder<PostgresServerResource> PublishAsDokployDatabase(
        this IResourceBuilder<PostgresServerResource> builder)
    {
        if (builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            builder.Resource.Annotations.Add(
                new DokployDatabaseAnnotation(DokployDatabaseType.Postgres));
        }

        return builder;
    }

    /// <summary>
    /// Configures a standard Redis resource to be provisioned as a Dokploy-native
    /// database in publish mode. In run mode, it continues to run as a local container.
    /// </summary>
    /// <param name="builder">The Redis resource builder (from <c>AddRedis()</c>).</param>
    /// <returns>The resource builder for chaining.</returns>
    /// <example>
    /// <code>
    /// var redis = builder.AddRedis("redis").PublishAsDokployDatabase();
    /// </code>
    /// </example>
    public static IResourceBuilder<RedisResource> PublishAsDokployDatabase(
        this IResourceBuilder<RedisResource> builder)
    {
        if (builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            builder.Resource.Annotations.Add(
                new DokployDatabaseAnnotation(DokployDatabaseType.Redis));
        }

        return builder;
    }

    /// <summary>
    /// Configures a standard MySQL resource to be provisioned as a Dokploy-native
    /// MySQL database in publish mode. In run mode, it continues to run as a local container.
    /// </summary>
    /// <param name="builder">The MySQL resource builder (from <c>AddMySql()</c>).</param>
    /// <returns>The resource builder for chaining.</returns>
    /// <example>
    /// <code>
    /// var mysql = builder.AddMySql("mysql").PublishAsDokployDatabase();
    /// </code>
    /// </example>
    public static IResourceBuilder<MySqlServerResource> PublishAsDokployDatabase(
        this IResourceBuilder<MySqlServerResource> builder)
    {
        if (builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            builder.Resource.Annotations.Add(
                new DokployDatabaseAnnotation(DokployDatabaseType.MySql));
        }

        return builder;
    }

    /// <summary>
    /// Configures a standard MySQL resource to be provisioned as a Dokploy-native
    /// MariaDB database in publish mode. In run mode, it continues to run as a local MySQL container
    /// (MariaDB is wire-compatible with MySQL).
    /// </summary>
    /// <param name="builder">The MySQL resource builder (from <c>AddMySql()</c>).</param>
    /// <returns>The resource builder for chaining.</returns>
    /// <remarks>
    /// MariaDB uses the MySQL Aspire hosting package since they are wire-compatible.
    /// Use this method instead of <see cref="PublishAsDokployDatabase(IResourceBuilder{MySqlServerResource})"/>
    /// when you want Dokploy to provision a MariaDB instance.
    /// </remarks>
    /// <example>
    /// <code>
    /// var mariadb = builder.AddMySql("mariadb").PublishAsDokployMariaDB();
    /// </code>
    /// </example>
    public static IResourceBuilder<MySqlServerResource> PublishAsDokployMariaDB(
        this IResourceBuilder<MySqlServerResource> builder)
    {
        if (builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            builder.Resource.Annotations.Add(
                new DokployDatabaseAnnotation(DokployDatabaseType.MariaDB));
        }

        return builder;
    }

    /// <summary>
    /// Configures a standard MongoDB resource to be provisioned as a Dokploy-native
    /// database in publish mode. In run mode, it continues to run as a local container.
    /// </summary>
    /// <param name="builder">The MongoDB resource builder (from <c>AddMongoDB()</c>).</param>
    /// <returns>The resource builder for chaining.</returns>
    /// <example>
    /// <code>
    /// var mongo = builder.AddMongoDB("mongo").PublishAsDokployDatabase();
    /// </code>
    /// </example>
    public static IResourceBuilder<MongoDBServerResource> PublishAsDokployDatabase(
        this IResourceBuilder<MongoDBServerResource> builder)
    {
        if (builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            builder.Resource.Annotations.Add(
                new DokployDatabaseAnnotation(DokployDatabaseType.MongoDB));
        }

        return builder;
    }

    // ── RunAsExisting ───────────────────────────────────────────────────

    /// <summary>
    /// Configures the PostgreSQL resource to connect to an existing database in run mode
    /// instead of spinning up a local Docker container.
    /// </summary>
    /// <param name="builder">The PostgreSQL resource builder.</param>
    /// <param name="connectionString">The connection string to the existing database.</param>
    /// <returns>The resource builder for chaining.</returns>
    public static IResourceBuilder<PostgresServerResource> RunAsExisting(
        this IResourceBuilder<PostgresServerResource> builder,
        string connectionString)
    {
        return RunAsExistingCore(builder, connectionString);
    }

    /// <inheritdoc cref="RunAsExisting(IResourceBuilder{PostgresServerResource}, string)"/>
    /// <param name="builder">The PostgreSQL resource builder.</param>
    /// <param name="connectionString">The parameter resource providing the connection string.</param>
    public static IResourceBuilder<PostgresServerResource> RunAsExisting(
        this IResourceBuilder<PostgresServerResource> builder,
        IResourceBuilder<ParameterResource> connectionString)
    {
        return RunAsExistingCore(builder, connectionString);
    }

    /// <summary>
    /// Configures the Redis resource to connect to an existing instance in run mode
    /// instead of spinning up a local Docker container.
    /// </summary>
    /// <param name="builder">The Redis resource builder.</param>
    /// <param name="connectionString">The connection string to the existing database.</param>
    /// <returns>The resource builder for chaining.</returns>
    public static IResourceBuilder<RedisResource> RunAsExisting(
        this IResourceBuilder<RedisResource> builder,
        string connectionString)
    {
        return RunAsExistingCore(builder, connectionString);
    }

    /// <inheritdoc cref="RunAsExisting(IResourceBuilder{RedisResource}, string)"/>
    /// <param name="builder">The Redis resource builder.</param>
    /// <param name="connectionString">The parameter resource providing the connection string.</param>
    public static IResourceBuilder<RedisResource> RunAsExisting(
        this IResourceBuilder<RedisResource> builder,
        IResourceBuilder<ParameterResource> connectionString)
    {
        return RunAsExistingCore(builder, connectionString);
    }

    /// <summary>
    /// Configures the MySQL resource to connect to an existing database in run mode
    /// instead of spinning up a local Docker container.
    /// </summary>
    /// <param name="builder">The MySQL resource builder.</param>
    /// <param name="connectionString">The connection string to the existing database.</param>
    /// <returns>The resource builder for chaining.</returns>
    public static IResourceBuilder<MySqlServerResource> RunAsExisting(
        this IResourceBuilder<MySqlServerResource> builder,
        string connectionString)
    {
        return RunAsExistingCore(builder, connectionString);
    }

    /// <inheritdoc cref="RunAsExisting(IResourceBuilder{MySqlServerResource}, string)"/>
    /// <param name="builder">The MySQL resource builder.</param>
    /// <param name="connectionString">The parameter resource providing the connection string.</param>
    public static IResourceBuilder<MySqlServerResource> RunAsExisting(
        this IResourceBuilder<MySqlServerResource> builder,
        IResourceBuilder<ParameterResource> connectionString)
    {
        return RunAsExistingCore(builder, connectionString);
    }

    /// <summary>
    /// Configures the MongoDB resource to connect to an existing database in run mode
    /// instead of spinning up a local Docker container.
    /// </summary>
    /// <param name="builder">The MongoDB resource builder.</param>
    /// <param name="connectionString">The connection string to the existing database.</param>
    /// <returns>The resource builder for chaining.</returns>
    public static IResourceBuilder<MongoDBServerResource> RunAsExisting(
        this IResourceBuilder<MongoDBServerResource> builder,
        string connectionString)
    {
        return RunAsExistingCore(builder, connectionString);
    }

    /// <inheritdoc cref="RunAsExisting(IResourceBuilder{MongoDBServerResource}, string)"/>
    /// <param name="builder">The MongoDB resource builder.</param>
    /// <param name="connectionString">The parameter resource providing the connection string.</param>
    public static IResourceBuilder<MongoDBServerResource> RunAsExisting(
        this IResourceBuilder<MongoDBServerResource> builder,
        IResourceBuilder<ParameterResource> connectionString)
    {
        return RunAsExistingCore(builder, connectionString);
    }

    // ── PublishAsExisting ───────────────────────────────────────────────

    /// <summary>
    /// Configures the PostgreSQL resource to connect to an existing Dokploy-provisioned database
    /// in publish mode instead of creating a new one.
    /// </summary>
    /// <param name="builder">The PostgreSQL resource builder.</param>
    /// <param name="connectionString">The connection string to the existing database.</param>
    /// <returns>The resource builder for chaining.</returns>
    public static IResourceBuilder<PostgresServerResource> PublishAsExisting(
        this IResourceBuilder<PostgresServerResource> builder,
        string connectionString)
    {
        return PublishAsExistingCore(builder, connectionString);
    }

    /// <inheritdoc cref="PublishAsExisting(IResourceBuilder{PostgresServerResource}, string)"/>
    /// <param name="builder">The PostgreSQL resource builder.</param>
    /// <param name="connectionString">The parameter resource providing the connection string.</param>
    public static IResourceBuilder<PostgresServerResource> PublishAsExisting(
        this IResourceBuilder<PostgresServerResource> builder,
        IResourceBuilder<ParameterResource> connectionString)
    {
        return PublishAsExistingCore(builder, connectionString);
    }

    /// <summary>
    /// Configures the Redis resource to connect to an existing Dokploy-provisioned instance
    /// in publish mode instead of creating a new one.
    /// </summary>
    /// <param name="builder">The Redis resource builder.</param>
    /// <param name="connectionString">The connection string to the existing database.</param>
    /// <returns>The resource builder for chaining.</returns>
    public static IResourceBuilder<RedisResource> PublishAsExisting(
        this IResourceBuilder<RedisResource> builder,
        string connectionString)
    {
        return PublishAsExistingCore(builder, connectionString);
    }

    /// <inheritdoc cref="PublishAsExisting(IResourceBuilder{RedisResource}, string)"/>
    /// <param name="builder">The Redis resource builder.</param>
    /// <param name="connectionString">The parameter resource providing the connection string.</param>
    public static IResourceBuilder<RedisResource> PublishAsExisting(
        this IResourceBuilder<RedisResource> builder,
        IResourceBuilder<ParameterResource> connectionString)
    {
        return PublishAsExistingCore(builder, connectionString);
    }

    /// <summary>
    /// Configures the MySQL resource to connect to an existing Dokploy-provisioned database
    /// in publish mode instead of creating a new one.
    /// </summary>
    /// <param name="builder">The MySQL resource builder.</param>
    /// <param name="connectionString">The connection string to the existing database.</param>
    /// <returns>The resource builder for chaining.</returns>
    public static IResourceBuilder<MySqlServerResource> PublishAsExisting(
        this IResourceBuilder<MySqlServerResource> builder,
        string connectionString)
    {
        return PublishAsExistingCore(builder, connectionString);
    }

    /// <inheritdoc cref="PublishAsExisting(IResourceBuilder{MySqlServerResource}, string)"/>
    /// <param name="builder">The MySQL resource builder.</param>
    /// <param name="connectionString">The parameter resource providing the connection string.</param>
    public static IResourceBuilder<MySqlServerResource> PublishAsExisting(
        this IResourceBuilder<MySqlServerResource> builder,
        IResourceBuilder<ParameterResource> connectionString)
    {
        return PublishAsExistingCore(builder, connectionString);
    }

    /// <summary>
    /// Configures the MongoDB resource to connect to an existing Dokploy-provisioned database
    /// in publish mode instead of creating a new one.
    /// </summary>
    /// <param name="builder">The MongoDB resource builder.</param>
    /// <param name="connectionString">The connection string to the existing database.</param>
    /// <returns>The resource builder for chaining.</returns>
    public static IResourceBuilder<MongoDBServerResource> PublishAsExisting(
        this IResourceBuilder<MongoDBServerResource> builder,
        string connectionString)
    {
        return PublishAsExistingCore(builder, connectionString);
    }

    /// <inheritdoc cref="PublishAsExisting(IResourceBuilder{MongoDBServerResource}, string)"/>
    /// <param name="builder">The MongoDB resource builder.</param>
    /// <param name="connectionString">The parameter resource providing the connection string.</param>
    public static IResourceBuilder<MongoDBServerResource> PublishAsExisting(
        this IResourceBuilder<MongoDBServerResource> builder,
        IResourceBuilder<ParameterResource> connectionString)
    {
        return PublishAsExistingCore(builder, connectionString);
    }

    // ── Private helpers ────────────────────────────────────────────────

    private static IResourceBuilder<T> RunAsExistingCore<T>(
        IResourceBuilder<T> builder,
        string connectionString)
        where T : IResource
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString, nameof(connectionString));
        builder.Resource.Annotations.Add(new DokployExistingDatabaseAnnotation(connectionString));
        return builder;
    }

    private static IResourceBuilder<T> RunAsExistingCore<T>(
        IResourceBuilder<T> builder,
        IResourceBuilder<ParameterResource> connectionString)
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        builder.Resource.Annotations.Add(
            new DokployExistingDatabaseAnnotation(connectionString.Resource));
        return builder;
    }

    private static IResourceBuilder<T> PublishAsExistingCore<T>(
        IResourceBuilder<T> builder,
        string connectionString)
        where T : IResource
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString, nameof(connectionString));
        RemoveDokployDatabaseAnnotation(builder.Resource);
        builder.Resource.Annotations.Add(new DokployExistingDatabaseAnnotation(connectionString));
        return builder;
    }

    private static IResourceBuilder<T> PublishAsExistingCore<T>(
        IResourceBuilder<T> builder,
        IResourceBuilder<ParameterResource> connectionString)
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        RemoveDokployDatabaseAnnotation(builder.Resource);
        builder.Resource.Annotations.Add(
            new DokployExistingDatabaseAnnotation(connectionString.Resource));
        return builder;
    }

    /// <summary>
    /// Removes the <see cref="DokployDatabaseAnnotation"/> from a resource if present.
    /// </summary>
    private static void RemoveDokployDatabaseAnnotation(IResource resource)
    {
        var annotations = resource.Annotations;
        var dokployAnnotation = annotations.OfType<DokployDatabaseAnnotation>().FirstOrDefault();
        if (dokployAnnotation is not null)
        {
            annotations.Remove(dokployAnnotation);
        }
    }
}
