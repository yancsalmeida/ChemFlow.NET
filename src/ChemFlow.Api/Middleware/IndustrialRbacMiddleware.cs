using Microsoft.AspNetCore.Mvc;

namespace ChemFlow.Api.Middleware;

public enum IndustrialRole
{
    Anonymous = 0,
    Viewer = 1,
    Operator = 2,
    Engineer = 3
}

/// <summary>
/// API-Key / Bearer token RBAC middleware protecting ChemFlow.NET SIL supervisory
/// actuation and disturbance endpoints.
/// </summary>
public sealed class IndustrialRbacMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<IndustrialRbacMiddleware> _logger;
    private readonly bool _enforceRbac;
    private readonly string _viewerKey;
    private readonly string _operatorKey;
    private readonly string _engineerKey;

    public IndustrialRbacMiddleware(
        RequestDelegate next,
        ILogger<IndustrialRbacMiddleware> logger,
        IConfiguration configuration)
    {
        _next = next;
        _logger = logger;
        _enforceRbac = ParseBoolEnv("INDUSTRIAL_ENFORCE_RBAC")
            ?? configuration.GetValue<bool>("IndustrialAuth:EnforceRbac");
        _viewerKey = Environment.GetEnvironmentVariable("INDUSTRIAL_VIEWER_KEY")
            ?? configuration["IndustrialAuth:ViewerKey"]
            ?? "viewer-ro-key";
        _operatorKey = Environment.GetEnvironmentVariable("INDUSTRIAL_OPERATOR_KEY")
            ?? configuration["IndustrialAuth:OperatorKey"]
            ?? "operator-ctrl-key";
        _engineerKey = Environment.GetEnvironmentVariable("INDUSTRIAL_ENGINEER_KEY")
            ?? configuration["IndustrialAuth:EngineerKey"]
            ?? "engineer-admin-key";
    }

    public async Task InvokeAsync(HttpContext context)
    {
        IndustrialRole required = GetRequiredRole(context.Request.Method, context.Request.Path.Value ?? string.Empty);
        string? suppliedKey = ExtractCredential(context.Request);

        IndustrialRole actor = IndustrialRole.Anonymous;
        if (!string.IsNullOrWhiteSpace(suppliedKey))
        {
            if (string.Equals(suppliedKey, _engineerKey, StringComparison.Ordinal))
                actor = IndustrialRole.Engineer;
            else if (string.Equals(suppliedKey, _operatorKey, StringComparison.Ordinal))
                actor = IndustrialRole.Operator;
            else if (string.Equals(suppliedKey, _viewerKey, StringComparison.Ordinal))
                actor = IndustrialRole.Viewer;
            else
            {
                await WriteProblemAsync(context, StatusCodes.Status401Unauthorized, "Invalid API Key",
                    "The supplied X-Api-Key or Bearer token is not recognized.");
                return;
            }

            context.Response.OnStarting(() =>
            {
                context.Response.Headers["X-Auth-Role"] = actor.ToString();
                return Task.CompletedTask;
            });
        }

        if (required > IndustrialRole.Anonymous)
        {
            if (actor == IndustrialRole.Anonymous && _enforceRbac)
            {
                await WriteProblemAsync(context, StatusCodes.Status401Unauthorized, "Authentication Required",
                    $"Missing X-Api-Key or Bearer token for protected SIL endpoint requiring role '{required}'.");
                return;
            }

            if (actor != IndustrialRole.Anonymous && actor < required)
            {
                _logger.LogWarning(
                    "RBAC Forbidden: role {ActorRole} attempted {Method} {Path} requiring {RequiredRole}",
                    actor, context.Request.Method, context.Request.Path, required);
                await WriteProblemAsync(context, StatusCodes.Status403Forbidden, "Insufficient Role",
                    $"Role '{actor}' cannot execute '{context.Request.Method} {context.Request.Path}'. Required: '{required}'.");
                return;
            }
        }

        await _next(context);
    }

    private static IndustrialRole GetRequiredRole(string method, string path)
    {
        if (!HttpMethods.IsPost(method) && !HttpMethods.IsPut(method) &&
            !HttpMethods.IsPatch(method) && !HttpMethods.IsDelete(method))
        {
            return IndustrialRole.Anonymous;
        }

        if (path.StartsWith("/api/supervisory/modbus/disturbance", StringComparison.OrdinalIgnoreCase))
            return IndustrialRole.Engineer;

        if (path.StartsWith("/api/supervisory/modbus/start", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/supervisory/modbus/stop", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/supervisory/modbus/flush-historian", StringComparison.OrdinalIgnoreCase))
        {
            return IndustrialRole.Operator;
        }

        return IndustrialRole.Anonymous;
    }

    private static string? ExtractCredential(HttpRequest req)
    {
        if (req.Headers.TryGetValue("X-Api-Key", out var keyValues) && !string.IsNullOrWhiteSpace(keyValues))
            return keyValues.ToString().Trim();

        if (req.Headers.TryGetValue("Authorization", out var authValues))
        {
            string raw = authValues.ToString().Trim();
            if (raw.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return raw["Bearer ".Length..].Trim();
        }

        return null;
    }

    private static bool? ParseBoolEnv(string name)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        return bool.TryParse(raw, out bool val) ? val : null;
    }

    private static Task WriteProblemAsync(HttpContext context, int statusCode, string title, string detail)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";
        return context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail,
            Instance = context.Request.Path
        });
    }
}
