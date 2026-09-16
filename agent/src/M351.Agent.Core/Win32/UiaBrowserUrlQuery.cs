using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using M351.Agent.Core.Collectors;

namespace M351.Agent.Core.Win32;

/// <summary>
/// Leitura da janela do navegador em foco por UI Automation (Seção 6.2). Faz DUAS coisas, nesta
/// ordem de importância:
///
///  1. NOMES ACESSÍVEIS DA JANELA — proteção, não coleta. É onde os navegadores marcam a janela
///     anônima, e é a única forma de saber disso hoje: o Chrome NÃO marca mais o título Win32
///     (a janela anônima se chama "Página - Google Chrome", igual à normal), só o nome acessível
///     do painel da janela ("... - Google Chrome (Modo anônimo)"); o Edge marca nos dois. Esta
///     leitura acontece MESMO com a coleta de sites desligada, porque sem ela o agente não teria
///     como rebaixar a política de títulos em navegação anônima — que é promessa de produto.
///  2. BARRA DE ENDEREÇO — só quando a coleta de sites está ligada: da janela em foco, procura a
///     primeira barra de ferramentas e, dentro dela, o primeiro campo de edição (a omnibox no
///     Chromium, a urlbar no Firefox); sem barra de ferramentas legível, tenta o primeiro campo
///     de edição da janela. Busca DIRIGIDA (FindFirst), nunca varredura da árvore inteira.
///
/// TRÊS PROTEÇÕES, porque isto é COM cross-process dentro do loop de coleta:
///  1. THREAD PRÓPRIA, STA: o objeto de automação nasce e morre numa única thread dedicada; os
///     loops de coleta nunca bloqueiam numa chamada COM em thread do pool.
///  2. PRAZO: cada leitura tem <see cref="Timeout"/>. Navegador congelado devolve null e a
///     coleta segue — tempo de app continua sendo contado, só o domínio não aparece.
///  3. DISJUNTOR: <see cref="FailuresToTrip"/> falhas/estouros seguidos desligam a leitura por
///     <see cref="CooldownMinutes"/> minutos. Máquina onde UIA não funciona (política de
///     segurança, navegador exótico) para de pagar o custo em vez de tentar a cada 5 s.
///
/// O QUE ESTA CLASSE NUNCA FAZ: ler conteúdo de página, ler aba fora do foco, escrever em
/// controle, ou abrir qualquer janela que não seja a do <c>hwnd</c> recebido.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UiaBrowserUrlQuery : IBrowserUrlQuery, IDisposable
{
    /// <summary>Prazo de uma leitura. Acima disso a amostra é perdida de propósito.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(1_500);

    /// <summary>Falhas seguidas que abrem o disjuntor.</summary>
    public const int FailuresToTrip = 3;

    /// <summary>Minutos de silêncio depois do disjuntor abrir.</summary>
    public const int CooldownMinutes = 10;

    private readonly BlockingCollection<Request> _queue = new(new ConcurrentQueue<Request>(), 8);
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Action<Exception>? _onFailure;
    private readonly Thread _worker;

    /// <summary>Teto de filhos inspecionados na janela: um navegador tem 2 ou 3, não dezenas.</summary>
    private const int MaxWindowChildren = 8;

    private IUIAutomation? _automation;
    private IUIAutomationCondition? _editCondition;
    private IUIAutomationCondition? _toolbarCondition;
    private IUIAutomationCondition? _anyCondition;

    private int _consecutiveFailures;
    private DateTimeOffset? _disabledUntil;
    private bool _disposed;

    /// <param name="onFailure">
    /// Recebe a exceção de uma leitura que falhou (o helper liga no AgentErrorReporter, que vira
    /// AGENT_ERROR sem conteúdo). Sem ele a falha é silenciosa, como no resto da coleta.
    /// </param>
    public UiaBrowserUrlQuery(Func<DateTimeOffset>? utcNow = null, Action<Exception>? onFailure = null)
    {
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _onFailure = onFailure;
        _worker = new Thread(Loop)
        {
            IsBackground = true, // nunca segura o encerramento do helper
            Name = "m351-uia-url",
        };
        _worker.SetApartmentState(ApartmentState.STA); // COM de UI exige STA
        _worker.Start();
    }

    /// <summary>Disjuntor aberto? (exposto para o status do tray e para teste.)</summary>
    public bool TemporarilyDisabled => _disabledUntil is { } until && _utcNow() < until;

    public BrowserReading? Read(IntPtr hwnd, string processName, bool readUrl)
    {
        if (_disposed || hwnd == IntPtr.Zero) return null;
        if (!BrowserProcesses.IsBrowser(processName)) return null;
        if (TemporarilyDisabled) return null;

        var request = new Request(hwnd, readUrl);
        try
        {
            if (!_queue.TryAdd(request)) return null; // fila cheia: leitura anterior ainda presa
        }
        catch (Exception)
        {
            return null; // fila encerrada (Dispose em corrida)
        }

        if (!request.Done.Wait(Timeout))
        {
            request.Abandoned = true; // a thread de UIA não vai mais gastar tempo com esta
            RegisterFailure();
            return null;
        }

        if (request.Failed)
        {
            RegisterFailure();
            return null;
        }

        _consecutiveFailures = 0;
        return new BrowserReading(request.Url, request.WindowNames);
    }

    private void RegisterFailure()
    {
        if (++_consecutiveFailures < FailuresToTrip) return;
        _consecutiveFailures = 0;
        _disabledUntil = _utcNow().AddMinutes(CooldownMinutes);
    }

    // ------------------------------------------------------------ thread de UIA (STA)
    private void Loop()
    {
        foreach (var request in _queue.GetConsumingEnumerable())
        {
            if (request.Abandoned) { request.Done.Set(); continue; }

            try
            {
                ReadWindow(request);
            }
            catch (Exception ex)
            {
                // COM fora do ar, janela morta, acesso negado: a leitura simplesmente não existe
                request.Failed = true;
                ResetAutomation();
                try { _onFailure?.Invoke(ex); } catch (Exception) { /* diagnóstico nunca derruba */ }
            }
            finally
            {
                request.Done.Set();
            }
        }
    }

    private void ReadWindow(Request request)
    {
        var automation = _automation ??= (IUIAutomation)new CUIAutomation();
        automation.ElementFromHandle(request.Hwnd, out var window);
        if (window is null) return;

        try
        {
            request.WindowNames = ReadWindowNames(automation, window);

            // Salvaguarda EXTRA (a autoridade continua sendo o TitleMasker, que reavalia tudo):
            // reconhecida a janela como anônima, a barra de endereço nem chega a ser lida. Assim
            // "o agente não lê o endereço de uma janela anônima" é literal, e não só "lê e joga
            // fora" — é a frase que a página de transparência pode publicar sem asterisco.
            if (request.WantsUrl && !Privacy.TitleMasker.IsPrivateBrowsing(null, request.WindowNames))
            {
                request.Url = ReadAddressBar(automation, window);
            }
        }
        finally
        {
            Release(window);
        }
    }

    /// <summary>
    /// Nomes acessíveis da janela e dos seus filhos diretos. É aqui que "(Modo anônimo)" e
    /// "(InPrivate)" aparecem — o painel da janela do navegador carrega a marca mesmo quando o
    /// título Win32 não carrega. Só filhos DIRETOS, com teto: nada de varrer a árvore.
    /// </summary>
    private IReadOnlyList<string> ReadWindowNames(IUIAutomation automation, IUIAutomationElement window)
    {
        var names = new List<string>(3);
        AddName(window, names);

        _anyCondition ??= CreateTrueCondition(automation);
        window.FindAll(UiaTreeScope.Children, _anyCondition!, out var children);
        if (children is null) return names;

        try
        {
            children.get_Length(out var length);
            for (var i = 0; i < Math.Min(length, MaxWindowChildren); i++)
            {
                children.GetElement(i, out var child);
                if (child is null) continue;
                try { AddName(child, names); }
                finally { Release(child); }
            }
        }
        finally
        {
            Release(children);
        }

        return names;
    }

    private static void AddName(IUIAutomationElement element, List<string> names)
    {
        element.GetCurrentPropertyValue(UiaPropertyIds.Name, out var value);
        if (value is string name && name.Length > 0) names.Add(name);
    }

    private string? ReadAddressBar(IUIAutomation automation, IUIAutomationElement window)
    {
        _editCondition ??= CreateCondition(automation, UiaPropertyIds.ControlType, UiaControlTypeIds.Edit);
        _toolbarCondition ??= CreateCondition(automation, UiaPropertyIds.ControlType, UiaControlTypeIds.ToolBar);

        IUIAutomationElement? toolbar = null;
        IUIAutomationElement? edit = null;
        try
        {
            // 1) barra de ferramentas → campo de edição dela (omnibox/urlbar)
            window.FindFirst(UiaTreeScope.Descendants, _toolbarCondition!, out toolbar);
            if (toolbar is not null)
            {
                toolbar.FindFirst(UiaTreeScope.Descendants, _editCondition!, out edit);
            }

            // 2) sem barra legível: primeiro campo de edição da janela
            if (edit is null)
            {
                window.FindFirst(UiaTreeScope.Descendants, _editCondition!, out edit);
            }

            if (edit is null) return null;

            edit.GetCurrentPropertyValue(UiaPropertyIds.ValueValue, out var value);
            return value as string;
        }
        finally
        {
            Release(edit);
            Release(toolbar);
        }
    }

    private static IUIAutomationCondition CreateCondition(IUIAutomation automation, int propertyId, int value)
    {
        automation.CreatePropertyCondition(propertyId, value, out var condition);
        return condition ?? throw new InvalidOperationException("UIA não criou a condição de busca.");
    }

    private static IUIAutomationCondition CreateTrueCondition(IUIAutomation automation)
    {
        automation.CreateTrueCondition(out var condition);
        return condition ?? throw new InvalidOperationException("UIA não criou a condição de busca.");
    }

    private void ResetAutomation()
    {
        Release(_editCondition);
        Release(_toolbarCondition);
        Release(_anyCondition);
        Release(_automation);
        _editCondition = null;
        _toolbarCondition = null;
        _anyCondition = null;
        _automation = null;
    }

    private static void Release(object? comObject)
    {
        if (comObject is null || !Marshal.IsComObject(comObject)) return;
        try { Marshal.FinalReleaseComObject(comObject); }
        catch (Exception) { /* já liberado */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _queue.CompleteAdding(); } catch (Exception) { /* já encerrada */ }
    }

    private sealed class Request(IntPtr hwnd, bool wantsUrl)
    {
        public IntPtr Hwnd { get; } = hwnd;
        public bool WantsUrl { get; } = wantsUrl;
        public ManualResetEventSlim Done { get; } = new(false);
        public string? Url { get; set; }
        public IReadOnlyList<string> WindowNames { get; set; } = [];
        public bool Failed { get; set; }
        public volatile bool Abandoned;
    }
}
