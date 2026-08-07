using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Orb.Automate.Agents.HR.Api;

/// <summary>
/// Converts unhandled exceptions into RFC 7807 problem details responses.
/// </summary>
/// <param name="problemDetailsService">The service used to write problem details responses.</param>
/// <param name="logger">The logger used for unhandled exception events.</param>
public sealed class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    private static readonly Action<ILogger, string, string, Exception?> LogUnhandledException =
        LoggerMessage.Define<string, string>(LogLevel.Error, new EventId(1, nameof(GlobalExceptionHandler)),
            "Unhandled exception while processing {Method} {Path}");

    /// <summary>
    /// Handles an unhandled exception for the current HTTP request.
    /// </summary>
    /// <param name="httpContext">The current HTTP context.</param>
    /// <param name="exception">The exception that was thrown.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns><see langword="true"/> when a problem details response was written; otherwise, <see langword="false"/>.</returns>
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        LogUnhandledException(logger, httpContext.Request.Method, httpContext.Request.Path, exception);
        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "An unexpected error occurred.",
                Detail = "The request could not be completed. Use the trace identifier when contacting support.",
                Extensions = { ["traceId"] = httpContext.TraceIdentifier }
            },
            Exception = exception
        });
    }
}
