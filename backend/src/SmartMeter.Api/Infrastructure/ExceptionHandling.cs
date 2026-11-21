using System.Diagnostics;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using SmartMeter.Application.Common;

namespace SmartMeter.Api.Infrastructure;

/// <summary>Maps application exceptions to RFC 7807 problem details (no internals leak in production).</summary>
internal sealed class GlobalExceptionHandler(IProblemDetailsService problems, IHostEnvironment env, ILogger<GlobalExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        ProblemDetails problem;
        switch (exception)
        {
            case ValidationException v:
                var errors = v.Errors.GroupBy(e => string.IsNullOrEmpty(e.PropertyName) ? "request" : e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).Distinct().ToArray());
                problem = new ValidationProblemDetails(errors) { Status = 400, Title = "Validation failed" };
                break;
            case BadRequestException b:
                problem = new ProblemDetails { Status = 400, Title = "Bad request", Detail = b.Message };
                break;
            case NotFoundException n:
                problem = new ProblemDetails { Status = 404, Title = "Not found", Detail = n.Message };
                break;
            case ConflictException c:
                problem = new ProblemDetails { Status = 409, Title = "Conflict", Detail = c.Message };
                break;
            case OperationCanceledException when http.RequestAborted.IsCancellationRequested:
                http.Response.StatusCode = 499;   // client closed request (map panned away) - nothing to report
                return true;
            case BadHttpRequestException bad:
                problem = new ProblemDetails { Status = bad.StatusCode, Title = "Bad request", Detail = bad.Message };
                break;
            default:
                logger.LogError(exception, "Unhandled exception for {Method} {Path}", http.Request.Method, http.Request.Path);
                problem = new ProblemDetails
                {
                    Status = 500,
                    Title = "An unexpected error occurred",
                    Detail = env.IsDevelopment() ? exception.Message : "The error has been logged. Quote the trace id when contacting support.",
                };
                break;
        }
        problem.Extensions["traceId"] = Activity.Current?.Id ?? http.TraceIdentifier;
        http.Response.StatusCode = problem.Status ?? 500;
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = problem, Exception = exception });
    }
}
