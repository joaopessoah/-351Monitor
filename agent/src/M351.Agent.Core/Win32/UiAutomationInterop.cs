using System.Runtime.InteropServices;

namespace M351.Agent.Core.Win32;

// =============================================================================================
// Interop MÍNIMO de UI Automation — só o necessário para ler a BARRA DE ENDEREÇO do navegador
// em foco (Seção 6.2). Segue a regra do NativeMethods: interop escrito à mão, sem dependência
// externa e com a lista de capacidades FECHADA.
//
// POR QUE UI AUTOMATION, e por que só isto: a barra de endereço é um controle da INTERFACE do
// navegador (chrome nativo), não da página. Lê-la não liga a acessibilidade do conteúdo web, não
// percorre o DOM, não vê abas em segundo plano e não toca em histórico ou cookies. É a leitura
// mais estreita que responde "que site está em foco" — e é por isso que ela foi escolhida em vez
// de banco de histórico do navegador (veria navegação fora do foco, com URL completa) ou extensão
// (exigiria implantar extensão por GPO em cada navegador).
//
// NUNCA ADICIONAR AQUI (Princípio 2, mesma régua do NativeMethods): padrões de UIA que leiam
// conteúdo de página (TextPattern), que cliquem/alterem valor (InvokePattern, ValuePattern.Set),
// ou que percorram janelas que não sejam a do navegador em foco.
//
// VTABLE: as interfaces COM abaixo declaram os slots na ORDEM EXATA do UIAutomationClient.idl,
// com Reservado_N no lugar dos métodos que não usamos — a ordem é o contrato binário, e um slot
// fora de lugar chamaria a função errada. Só existem slots até o último método usado.
// =============================================================================================

/// <summary>IDs de propriedade do UIA (UIA_*PropertyId) usados aqui.</summary>
internal static class UiaPropertyIds
{
    internal const int Name = 30_005;
    internal const int ControlType = 30_003;
    internal const int ValueValue = 30_045;
}

/// <summary>IDs de tipo de controle (UIA_*ControlTypeId) usados aqui.</summary>
internal static class UiaControlTypeIds
{
    internal const int Edit = 50_004;
    internal const int ToolBar = 50_021;
}

/// <summary>Escopo de busca do UIA (enum TreeScope).</summary>
internal static class UiaTreeScope
{
    internal const int Children = 2;
    internal const int Descendants = 4;
}

/// <summary>CLSID_CUIAutomation — instanciar com <c>new CUIAutomation()</c> (CoCreateInstance).</summary>
[ComImport]
[Guid("ff48dba4-60ef-4201-aa87-54103eef594e")]
internal class CUIAutomation
{
}

/// <summary>IUIAutomationCondition — opaca para nós (só é criada e repassada).</summary>
[ComImport]
[Guid("352ffba8-0973-437c-a61f-f64cafd81df9")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationCondition
{
}

/// <summary>
/// IUIAutomation (IID 30cbe57d-d9d0-452a-ab13-7ac5ac4825ee). Slots usados:
/// 4 = ElementFromHandle, 21 = CreatePropertyCondition.
/// </summary>
[ComImport]
[Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomation
{
    void Reservado_CompareElements();
    void Reservado_CompareRuntimeIds();
    void Reservado_GetRootElement();

    void ElementFromHandle(IntPtr hwnd, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement? element);

    void Reservado_ElementFromPoint();
    void Reservado_GetFocusedElement();
    void Reservado_GetRootElementBuildCache();
    void Reservado_ElementFromHandleBuildCache();
    void Reservado_ElementFromPointBuildCache();
    void Reservado_GetFocusedElementBuildCache();
    void Reservado_CreateTreeWalker();
    void Reservado_get_ControlViewWalker();
    void Reservado_get_ContentViewWalker();
    void Reservado_get_RawViewWalker();
    void Reservado_get_RawViewCondition();
    void Reservado_get_ControlViewCondition();
    void Reservado_get_ContentViewCondition();
    void Reservado_CreateCacheRequest();

    void CreateTrueCondition([MarshalAs(UnmanagedType.Interface)] out IUIAutomationCondition? condition);

    void Reservado_CreateFalseCondition();

    void CreatePropertyCondition(
        int propertyId,
        [MarshalAs(UnmanagedType.Struct)] object value,
        [MarshalAs(UnmanagedType.Interface)] out IUIAutomationCondition? condition);
}

/// <summary>
/// Vetor de elementos devolvido por FindAll (IID 14314595-b4bc-4055-95f2-58f2e42c9855).
/// </summary>
[ComImport]
[Guid("14314595-b4bc-4055-95f2-58f2e42c9855")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationElementArray
{
    void get_Length(out int length);

    void GetElement(int index, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement? element);
}

/// <summary>
/// IUIAutomationElement (IID d22108aa-8ac5-49a5-837b-37bbb3d7591e). Slots usados:
/// 3 = FindFirst, 4 = FindAll, 8 = GetCurrentPropertyValue.
/// </summary>
[ComImport]
[Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationElement
{
    void Reservado_SetFocus();
    void Reservado_GetRuntimeId();

    void FindFirst(
        int scope,
        IUIAutomationCondition condition,
        [MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement? found);

    void FindAll(
        int scope,
        IUIAutomationCondition condition,
        [MarshalAs(UnmanagedType.Interface)] out IUIAutomationElementArray? found);

    void Reservado_FindFirstBuildCache();
    void Reservado_FindAllBuildCache();
    void Reservado_BuildUpdatedCache();

    void GetCurrentPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object? value);
}
