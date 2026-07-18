using Microsoft.AspNetCore.Mvc;

namespace ArtificeWorks.Api.Controllers;

/// <summary>
/// Base controller that produces RFC 7807 ProblemDetails responses carrying a
/// stable machine-readable <c>code</c> extension. Every non-2xx result an action
/// returns should go through <see cref="Problem"/> so the error contract stays
/// uniform across the API.
/// </summary>
public abstract class ApiControllerBase : ControllerBase
{
    protected ObjectResult Problem(int statusCode, string code, string detail, string? title = null)
    {
        // base.Problem builds the ProblemDetails via the registered factory (type,
        // traceId, correct application/problem+json content type); we then stamp the
        // machine-readable reason code as an extension member.
        var result = base.Problem(detail: detail, statusCode: statusCode, title: title);
        if (result.Value is ProblemDetails problem)
        {
            problem.Extensions["code"] = code;
        }
        return result;
    }
}
