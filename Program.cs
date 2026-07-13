using System.Net;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MySqlConnector;

EnvFileLoader.Load(Path.Combine(Directory.GetCurrentDirectory(), ".env"));

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 16 * 1024;
});

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();

var registryOptions = TenantRegistryOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(registryOptions);
builder.Services.AddSingleton<TenantEndpointRepository>();
builder.Services.AddHealthChecks()
    .AddCheck<TenantRegistryHealthCheck>("tenant_registry", tags: ["ready"]);

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("tenant-resolve", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseRateLimiter();

app.MapGet("/", () => Results.Ok(new
{
    service = "Tenant Endpoint Resolver",
    version = "v1"
}));

app.MapGet("/health/live", () => Results.Ok(new { status = "Healthy" }))
    .DisableRateLimiting();

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
}).DisableRateLimiting();

app.MapPost("/api/v1/tenants/resolve", async (
    ResolveTenantRequest request,
    TenantEndpointRepository repository,
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
{
    httpContext.Response.Headers.CacheControl = "no-store";

    var clientCode = request.ClientCode?.Trim().ToUpperInvariant() ?? string.Empty;
    if (!ClientCodeValidator.IsValid(clientCode))
        return TenantUnavailable();

    var tenant = await repository.ResolveAsync(clientCode, cancellationToken);
    if (tenant is null)
        return TenantUnavailable();

    return Results.Ok(new ResolveTenantResponse(
        tenant.ClientCode,
        tenant.ApiBaseUrl,
        tenant.ValidFromUtc,
        tenant.ValidUntilUtc,
        true));
})
.RequireRateLimiting("tenant-resolve");

app.Run();

static IResult TenantUnavailable() => Results.Json(
    new ApiError("TENANT_NOT_AVAILABLE", "Client code is invalid or unavailable."),
    statusCode: StatusCodes.Status404NotFound);

internal sealed record ResolveTenantRequest(string? ClientCode);

internal sealed record ResolveTenantResponse(
    string ClientCode,
    string ApiBaseUrl,
    DateTime ValidFromUtc,
    DateTime ValidUntilUtc,
    bool IsActive);

internal sealed record ApiError(string Code, string Message);

internal sealed record TenantEndpoint(
    string ClientCode,
    string ApiBaseUrl,
    DateTime ValidFromUtc,
    DateTime ValidUntilUtc);

internal static partial class ClientCodeValidator
{
    [GeneratedRegex("^[A-Z0-9][A-Z0-9_-]{1,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ClientCodePattern();

    public static bool IsValid(string clientCode) => ClientCodePattern().IsMatch(clientCode);
}

internal sealed class TenantEndpointRepository(TenantRegistryOptions options)
{
    public async Task<TenantEndpoint?> ResolveAsync(string clientCode, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT client_code, api_base_url, valid_from_utc, valid_until_utc
            FROM tenant_endpoints
            WHERE client_code = @ClientCode
              AND is_active = 1
              AND UTC_TIMESTAMP(6) >= valid_from_utc
              AND UTC_TIMESTAMP(6) < valid_until_utc
            LIMIT 1;
            """;
        command.Parameters.Add("@ClientCode", MySqlDbType.VarChar, 64).Value = clientCode;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var apiBaseUrl = reader.GetString("api_base_url");
        if (!Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("Tenant endpoint configuration contains an invalid API URL.");
        }

        return new TenantEndpoint(
            reader.GetString("client_code"),
            apiBaseUrl,
            DateTime.SpecifyKind(reader.GetDateTime("valid_from_utc"), DateTimeKind.Utc),
            DateTime.SpecifyKind(reader.GetDateTime("valid_until_utc"), DateTimeKind.Utc));
    }

    public async Task<bool> CanConnectAsync(CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1;";
        command.CommandTimeout = 5;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }
}

internal sealed class TenantRegistryHealthCheck(TenantEndpointRepository repository) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await repository.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy();
        }
        catch
        {
            return HealthCheckResult.Unhealthy();
        }
    }
}

internal sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var statusCode = exception is MySqlException or TimeoutException
            ? StatusCodes.Status503ServiceUnavailable
            : StatusCodes.Status500InternalServerError;

        logger.LogError(exception, "Tenant resolver request failed. TraceId: {TraceId}", httpContext.TraceIdentifier);

        httpContext.Response.StatusCode = statusCode;
        httpContext.Response.ContentType = "application/problem+json";
        httpContext.Response.Headers.CacheControl = "no-store";

        await httpContext.Response.WriteAsJsonAsync(new
        {
            type = "about:blank",
            title = statusCode == StatusCodes.Status503ServiceUnavailable
                ? "Service temporarily unavailable"
                : "Unexpected server error",
            status = statusCode,
            traceId = httpContext.TraceIdentifier
        }, cancellationToken);

        return true;
    }
}

internal sealed class TenantRegistryOptions
{
    public required string ConnectionString { get; init; }

    public static TenantRegistryOptions FromConfiguration(IConfiguration configuration)
    {
        var host = Required(configuration, "TENANT_REGISTRY_DB_HOST");
        var database = Required(configuration, "TENANT_REGISTRY_DB_NAME");
        var user = Required(configuration, "TENANT_REGISTRY_DB_USER");
        var password = Required(configuration, "TENANT_REGISTRY_DB_PASSWORD");
        var portText = configuration["TENANT_REGISTRY_DB_PORT"] ?? "3306";
        var sslModeText = configuration["TENANT_REGISTRY_DB_SSL_MODE"] ?? "Preferred";

        if (!uint.TryParse(portText, out var port) || port is 0 or > 65535)
            throw new InvalidOperationException("TENANT_REGISTRY_DB_PORT must be a valid TCP port.");

        if (!Enum.TryParse<MySqlSslMode>(sslModeText, true, out var sslMode))
            throw new InvalidOperationException("TENANT_REGISTRY_DB_SSL_MODE is invalid.");

        var connectionString = new MySqlConnectionStringBuilder
        {
            Server = host,
            Port = port,
            Database = database,
            UserID = user,
            Password = password,
            SslMode = sslMode,
            AllowPublicKeyRetrieval = sslMode == MySqlSslMode.None,
            Pooling = true,
            MinimumPoolSize = 0,
            MaximumPoolSize = 20,
            ConnectionTimeout = 10,
            DefaultCommandTimeout = 10
        }.ConnectionString;

        return new TenantRegistryOptions { ConnectionString = connectionString };
    }

    private static string Required(IConfiguration configuration, string key)
    {
        var value = configuration[key]?.Trim();
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Required environment variable '{key}' is missing.");
    }
}

internal static class EnvFileLoader
{
    public static void Load(string path)
    {
        if (!File.Exists(path))
            return;

        foreach (var sourceLine in File.ReadLines(path))
        {
            var line = sourceLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
                continue;

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();
            if (value.Length >= 2
                && ((value[0] == '"' && value[^1] == '"')
                    || (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key)))
                Environment.SetEnvironmentVariable(key, value);
        }
    }
}
