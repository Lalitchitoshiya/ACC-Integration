namespace Connector.Api.Http;

/// <summary>Single error shape for all endpoints, per specs/12-permissions-errors.md.</summary>
public record ApiError(string Error, string Message, object? Details = null)
{
    public static IResult ValidationFailed(string message, object? details = null) =>
        Results.Json(new ApiError("validation_failed", message, details), statusCode: 400);
    public static IResult Unauthenticated(string message = "Missing or invalid credentials.") =>
        Results.Json(new ApiError("unauthenticated", message), statusCode: 401);
    public static IResult Forbidden(string message = "Your role does not permit this action.") =>
        Results.Json(new ApiError("forbidden", message), statusCode: 403);
    public static IResult NotFound(string message) =>
        Results.Json(new ApiError("not_found", message), statusCode: 404);
    public static IResult Conflict(string message, object? details = null) =>
        Results.Json(new ApiError("conflict", message, details), statusCode: 409);
    public static IResult Unprocessable(string message) =>
        Results.Json(new ApiError("unprocessable", message), statusCode: 422);
    public static IResult UpstreamError(string message) =>
        Results.Json(new ApiError("upstream_error", message), statusCode: 502);
}
