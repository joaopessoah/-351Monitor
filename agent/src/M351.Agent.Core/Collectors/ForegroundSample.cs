namespace M351.Agent.Core.Collectors;

/// <summary>Amostra crua da janela em foco (antes do enforcement de privacidade).</summary>
public sealed record ForegroundSample(string ProcessName, string? ExePath, string? AppId, string? Title);

/// <summary>Abstração da consulta Win32 de janela ativa (mockável em teste).</summary>
public interface IForegroundWindowQuery
{
    /// <summary>null quando não há janela em foco (trocas de foco, sessão sem desktop) — sem crash.</summary>
    ForegroundSample? GetForegroundWindowInfo();
}

/// <summary>Abstração de GetLastInputInfo (mockável em teste).</summary>
public interface IIdleTimeQuery
{
    /// <summary>Milissegundos desde o último input do usuário na sessão.</summary>
    long GetIdleMilliseconds();
}
