using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using M351.Agent.Core.Logging;
using MonitorAgentService;
using Xunit;

namespace M351.Agent.Tests;

/// <summary>
/// Regressão do deadlock do aceite F1: com quota de buffer zero no pipe, o WriteLine
/// do SendConfig só completa quando o helper lê (rendezvous). Se o read-loop do helper
/// estiver preso (ex.: NoticeForm modal), o push de config congela — e, rodando síncrono
/// dentro do loop do BatchSender, congelava a cadência de envio inteira do serviço.
/// </summary>
public class PipeServerTests
{
    [Fact]
    public async Task SendConfig_nao_bloqueia_quando_o_helper_para_de_ler_o_pipe()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "m351-pipetest-" + Guid.NewGuid().ToString("N"));
        const int sessionId = 9991;
        using var runtime = AgentRuntime.Create(new NullLogSink(), dataDir);
        using var pipe = new PipeServer(sessionId, WindowsIdentity.GetCurrent().User, runtime,
            new NullLogSink(), onPipeDenied: () => { });
        pipe.Start();

        using var client = new NamedPipeClientStream(".", $"monitoragent.{sessionId}",
            PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5_000);
        using var reader = new StreamReader(client, Encoding.UTF8, false, 16 * 1024, leaveOpen: true);

        // consome a config inicial enviada na conexão (equivale ao único read que o
        // helper travado chegou a fazer antes do ShowDialog)
        Assert.NotNull(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));

        // a partir daqui o cliente deliberadamente NÃO posta mais nenhum read —
        // simula o read-loop do helper preso no notice.ShowDialog()
        var send = Task.Run(() => pipe.SendConfig());
        var done = await Task.WhenAny(send, Task.Delay(2_000));

        // com buffers 0/0 o WriteLine trava para sempre (era o bug); com quota > 0
        // a escrita cabe no buffer do pipe e retorna imediatamente
        Assert.Same(send, done);
    }
}
