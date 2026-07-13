# Tenant Endpoint Resolver API

Small .NET 8 API that resolves a client code to its currently active HRMS API endpoint.

## Local run

The real `.env` is intentionally ignored. Copy `.env.example` to `.env`, provide a least-privilege database account, then run:

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

Deploy with the included Dockerfile and configure every variable from `.env.example` as Coolify environment variables/secrets. Do not commit or copy the local `.env` into the image.

Health endpoints:

- `GET /health/live`
- `GET /health/ready`
