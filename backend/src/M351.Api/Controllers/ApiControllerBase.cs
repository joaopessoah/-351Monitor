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

    /// <summary>Intervalo máximo dos endpoints históricos: 92 dias (um trimestre, F3.2/F3.3).</summary>
    public const int MaxReportRangeDays = 92;

    /// <summary>
    /// Validação canônica de período (mesma régua do dashboard F3.2): from/to no fuso do
    /// tenant, inclusivos, yyyy-MM-dd; from &lt;= to e janela de no máximo 92 dias.
    /// Retorna o 400 ProblemDetails ou null quando válido.
    /// </summary>
    protected ObjectResult? ValidateRange(string? from, string? to, out DateOnly fromDay, out DateOnly toDay)
    {
        toDay = default;
        if (!DateOnly.TryParseExact(from, "yyyy-MM-dd", out fromDay))
            return ProblemResponse(StatusCodes.Status400BadRequest, "Parâmetro from é obrigatório no formato yyyy-MM-dd.");
        if (!DateOnly.TryParseExact(to, "yyyy-MM-dd", out toDay))
            return ProblemResponse(StatusCodes.Status400BadRequest, "Parâmetro to é obrigatório no formato yyyy-MM-dd.");
        if (fromDay > toDay)
            return ProblemResponse(StatusCodes.Status400BadRequest, "Intervalo inválido: from deve ser anterior ou igual a to.");
        if (toDay.DayNumber - fromDay.DayNumber + 1 > MaxReportRangeDays)
            return ProblemResponse(StatusCodes.Status400BadRequest, $"Intervalo máximo de {MaxReportRangeDays} dias.");
        return null;
    }
}
