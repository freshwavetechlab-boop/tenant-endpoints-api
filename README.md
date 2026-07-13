# Tenant Endpoint Resolver API

Small .NET 8 API that resolves a client code to its currently active HRMS API endpoint.

## Local run

The real `appsettings.Development.json` is intentionally ignored. Copy
`appsettings.Development.example.json` to `appsettings.Development.json`, provide a
least-privilege database connection string, then run:

```powershell
dotnet run
```

Request:

```http
POST /api/v1/tenants/resolve
Content-Type: application/json

{
  "clientCode": "GAD"
}
```

Only clients whose record is active and inside its UTC validity window are returned. Unknown, inactive, future and expired codes receive the same `404 TENANT_NOT_AVAILABLE` response.

## Coolify

Deploy with the included Dockerfile and configure the connection string as the
Coolify secret `ConnectionStrings__TenantRegistry`. Do not commit or copy the local
`appsettings.Development.json` into the image.

Health endpoints:

- `GET /health/live`
- `GET /health/ready`
