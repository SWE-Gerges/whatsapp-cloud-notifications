using Microsoft.AspNetCore.Diagnostics;
using Whatsapp.Abstractions;

namespace Whatsapp.Api.Infrastructure;

/// <summary>
/// Turns known domain exceptions into problem details. Anything else is left to the default exception handler,
/// which answers with a generic 500 problem that carries no stack trace or exception message.
/// </summary>
internal sealed class ApiExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not NotificationValidationException validation)
        {
            return false;
        }

        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails =
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "The notification is not valid.",
                Detail = validation.Message,
            },
        });
    }
}
