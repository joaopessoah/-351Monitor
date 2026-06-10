using Microsoft.AspNetCore.Mvc;

namespace M351.Api.Controllers;

/// <summary>Base dos controllers do portal: erros sempre como ProblemDetails (RFC 9457).</summary>
[ApiController]
public abstract class ApiControllerBase : ControllerBase
{
    protected ObjectResult ProblemResponse(int statusCode, string title, string? detail = null, string? code = null)
    {
        var problem = ProblemDetailsFactory.CreateProblemDetails(
            HttpContext, statusCode: statusCode, title: title, detail: detail);

        if (code is not null)
        {
            problem.Extensions["code"] = code;
        }

        return new ObjectResult(problem)
        {
            StatusCode = statusCode,
            ContentTypes = { "application/problem+json" },
        };
    }

    /// <summary>404 padrão: recurso inexistente OU de outro tenant (nunca 403 — Princípio 4).</summary>
    protected ObjectResult NotFoundProblem() =>
        ProblemResponse(StatusCodes.Status404NotFound, "Recurso não encontrado.");
}
