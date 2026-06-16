# Kit de instalacao do agente +351 Monitor (TI do cliente)

Guia operacional para a equipe de TI instalar, validar e diagnosticar o agente +351 Monitor
em estacoes Windows. Tudo aqui descreve o comportamento real do agente desta versao. Onde um
recurso nao existe, isso esta dito explicitamente.

O agente sao dois processos por maquina:

- `MonitorAgentService.exe` - servico Windows `MonitorAgentService`, conta LocalSystem, Session 0.
  Faz enroll, envio em lote, auto-update e supervisao do helper.
- `MonitorAgentSession.exe` - helper de sessao, roda com o token do proprio usuario logado (baixo
  privilegio). Mostra o icone na bandeja e a janela de transparencia. Ha um helper por sessao
  interativa.

---

## 1. Pre-requisitos

### Sistema operacional

- Windows 10 versao 1809 ou superior, ou Windows 11.
- Arquitetura x64 apenas. Nao existe build arm64 nesta versao. Maquinas ARM (ex.: Surface Pro X,
  Windows on ARM) nao sao suportadas.
- SKU Server e cenarios RDS/Citrix/Terminal Server nao sao suportados nesta versao. O agente nao
  bloqueia a instalacao nesses SKUs, mas o ambiente multi-sessao nao e um caso testado; o portal
  pode marcar o device como tipo "server" / nao suportado. Nao instale em servidores de sessao no
  piloto.

### Conta e privilegio

- A instalacao do MSI exige privilegio de administrador (MSI per-machine). Distribua por uma
  ferramenta que rode elevado (GPO, Intune, RMM) ou execute como admin.
- O servico roda como LocalSystem. Parar ou desinstalar o servico exige administrador (DACL
  padrao do SCM, nao alterada nesta versao). Um usuario comum nao consegue parar o servico, mas
  consegue matar o processo do helper na propria sessao (tratado pelo watchdog, ver item 4).

### Rede

- Conectividade HTTPS de saida da estacao ate o `SERVERURL` informado na instalacao (ex.:
  `https://api.seu-dominio.com.br`). O agente fala HTTPS apenas com esse host.
- Timeout de requisicao do agente: 30 s. Backoff de reenvio em caso de falha: 5 s, 10 s, 30 s,
  1 min, 5 min, 10 min (teto), com jitter. Eventos ficam preservados na fila local enquanto a
  estacao estiver sem conexao.
- Proxy: por padrao o agente usa o proxy de SISTEMA (WinHTTP) ou conexao direta, sem configuracao
  adicional. Se a estacao usa um proxy que o WinHTTP nao herda, informe `PROXYURL=` na instalacao
  (ver item 2). O proxy declarado usa as credenciais padrao do contexto do servico
  (`UseDefaultCredentials`); proxy que exige usuario/senha explicitos nao e suportado nesta versao.
- A validacao de certificado TLS NUNCA e desabilitada pelo agente. Inspecao TLS/MITM corporativa
  que reescreva o certificado fara o handshake falhar e sera reportada como "erro de certificado"
  (ver item 4). O host do `SERVERURL` precisa ser confiavel na cadeia de certificados da estacao
  ou estar na lista de bypass do equipamento de inspecao.

### Pastas e portas locais

- Binarios: `%ProgramFiles%\M351\MonitorAgent\`.
- Dados, fila e logs: `%ProgramData%\M351\MonitorAgent\` (ACL restrita a SYSTEM e Administradores).
  Logs em `...\logs\` (`service-*.log`, `session-{sid}-*.log`), rotacao diaria, 5 MB por arquivo,
  maximo 10 arquivos.
- Comunicacao servico <-> helper e por named pipe local com DACL restrita ao SID do usuario da
  sessao e ao SYSTEM. Nao usa porta TCP.

---

## 2. Instalacao silenciosa

### Linha de comando basica

```
msiexec /i MonitorAgent.msi /qn ENROLLKEY=ek_XXXXXXXXXXXX SERVERURL=https://api.seu-dominio.com.br
```

Propriedades aceitas (todas maiusculas, publicas):

| Propriedade | Obrigatoria | Para que serve |
|---|---|---|
| `ENROLLKEY` | Sim (salvo `NOENROLL=1`) | Enrollment key do tenant, prefixo `ek_`. Registra a maquina no enroll. |
| `SERVERURL` | Sim | Base URL HTTPS do servidor (sem barra final necessaria). |
| `PROXYURL` | Nao | Proxy corporativo explicito quando o proxy de sistema nao basta. |
| `NOENROLL` | Nao | `NOENROLL=1` adia o enroll para o primeiro boot real (golden image). |

Notas:

- Na instalacao normal o MSI grava o `SERVERURL`/`PROXYURL` em
  `%ProgramData%\M351\MonitorAgent\install.json` e dispara o enroll logo apos o servico subir.
  Uma falha de rede no enroll NAO reverte a instalacao: o servico re-tenta o enroll a cada 1 h e
  no proximo boot.
- O servico e instalado como Automatic (Delayed Start). Recovery do SCM configurado para
  reiniciar o servico em 10 s, 60 s e 300 s (reset de contagem em 1 dia).

### Golden image (NOENROLL)

Para preparar uma imagem mestre que sera clonada:

```
msiexec /i MonitorAgent.msi /qn NOENROLL=1 SERVERURL=https://api.seu-dominio.com.br ENROLLKEY=ek_XXXXXXXXXXXX
```

Com `NOENROLL=1` a enrollment key fica pendente no `install.json` e o enroll so acontece no
PRIMEIRO boot real de cada clone. Isso evita que todas as maquinas clonadas compartilhem a mesma
identidade. Apos o primeiro enroll bem-sucedido a key pendente e removida do arquivo.

### Desinstalacao

```
msiexec /x MonitorAgent.msi /qn
```

Exige administrador. Gera flush final da fila e um evento `AGENT_STOP` com motivo "uninstall".
Os dados em `%ProgramData%` (fila, identidade, logs) NAO sao apagados pelo desinstalador nem pelo
upgrade; a remocao desses dados e uma acao manual e explicita do operador.

### Distribuicao por GPO

1. Coloque `MonitorAgent.msi` num share de rede acessivel a todas as estacoes (leitura para
   `Domain Computers`).
2. Como as propriedades precisam ser passadas (`ENROLLKEY`, `SERVERURL`), a forma mais simples e
   distribuir via script de inicializacao de computador (Computer Startup Script) chamando
   `msiexec /i \\share\MonitorAgent.msi /qn ENROLLKEY=... SERVERURL=...`. A publicacao direta de
   pacote por GPO Software Installation nao permite informar propriedades sem um transform `.mst`.
3. Se preferir GPO Software Installation, gere um transform `.mst` (ex.: com Orca) fixando
   `ENROLLKEY` e `SERVERURL` e associe o transform ao pacote.

### Distribuicao por Intune

1. Empacote o MSI como aplicativo Win32 (`.intunewin`) ou use o tipo "Linha de comando".
2. Comando de instalacao:
   `msiexec /i MonitorAgent.msi /qn ENROLLKEY=ek_XXXX SERVERURL=https://api.seu-dominio.com.br`
3. Comando de desinstalacao: `msiexec /x MonitorAgent.msi /qn`.
4. Regra de deteccao: presenca do servico `MonitorAgentService` ou do arquivo
   `%ProgramFiles%\M351\MonitorAgent\MonitorAgentService.exe`.

> Aviso sobre assinatura: nesta versao o MSI pode ainda nao estar assinado com Authenticode. Em
> instalacao silenciosa `/qn` o SmartScreen nao bloqueia, mas a execucao por duplo-clique mostra
> aviso. Para o piloto, prefira sempre a instalacao silenciosa gerenciada.

---

## 3. Verificacao pos-instalacao

Em uma estacao recem-instalada, confirme:

1. Servico presente e em execucao, no modo Automatic (Delayed Start):
   ```
   sc qc MonitorAgentService
   sc query MonitorAgentService
   ```
   O tipo de inicio deve aparecer como `AUTO_START` com a flag de delayed start; estado `RUNNING`.
2. Dois processos ativos:
   - `MonitorAgentService.exe` na Session 0 (servico).
   - `MonitorAgentSession.exe` na sessao do usuario logado (helper). Verifique no Gerenciador de
     Tarefas, aba Detalhes, coluna ID da Sessao.
3. Icone na bandeja do sistema, com tooltip "Monitoramento corporativo ativo". Ao clicar com o
   botao direito, o menu tem: "O que esta sendo coletado agora", "Politica de monitoramento",
   "Status da conexao" e "Sobre". Nao existe item "Sair" (por design o agente e sempre visivel e
   nao tem modo oculto).
4. O dispositivo aparece no portal (tela Dispositivos) em menos de 2 minutos, com versao do agente
   e ultimo contato preenchidos.
5. (Opcional) Pastas criadas: `%ProgramFiles%\M351\MonitorAgent\` (binarios) e
   `%ProgramData%\M351\MonitorAgent\` (dados/fila/logs).

Para validar o estado da conexao na propria estacao, abra "Status da conexao" no menu da bandeja.
Em uma instalacao saudavel ele mostra "Servidor: conectado ao servidor" e o horario do ultimo
envio.

---

## 4. Erros comuns e troubleshooting

Os sintomas abaixo sao observaveis em dois lugares:

- Na estacao: menu da bandeja "Status da conexao" (canal local + estado do servidor + ultimo
  envio) e a janela "O que esta sendo coletado agora".
- No portal: tela Dispositivos / painel de saude, que sinaliza por device "Sem comunicacao",
  "Relogio dessincronizado", "Versao desatualizada", "Adulteracao detectada" e "Ciencia pendente".

Os logs do agente ficam em `%ProgramData%\M351\MonitorAgent\logs\`. Para coletar tudo de forma
sanitizada, gere o pacote de suporte (item 5).

### 4.1 Enrollment key invalida, expirada, revogada, esgotada ou limite de devices do plano

- **Sintoma**
  - Estacao: "Status da conexao" mostra "Servidor: dispositivo ainda nao registrado". O device
    nao aparece no portal.
  - Log do servico: "Enroll rejeitado pelo servidor: HTTP 403" (key inexistente, revogada,
    expirada ou esgotada por limite de usos) ou "HTTP 422" (limite de dispositivos do plano).
  - O comando manual `MonitorAgentService.exe --enroll ek_... --server <url>` (no diretorio de
    binarios, como admin) retorna a mensagem "Falha no enrollment - verifique a chave (ek_...), a
    URL do servidor e a rede".
- **Causa**
  - 403: a enrollment key nao existe, foi revogada, expirou ou atingiu o limite de usos. O
    servidor nao distingue esses casos para quem esta de fora (resposta unica).
  - 422 `device_limit_exceeded`: o plano do tenant atingiu o numero maximo de dispositivos ativos
    (ex.: limite do trial). Vale apenas para device NOVO; um re-enroll da mesma maquina
    (mesmo fingerprint) nao consome cota.
- **Acao**
  - Gere uma enrollment key valida no portal/backoffice e reinstale com `ENROLLKEY` correto, ou
    rode o enroll manual com a key nova. A maquina re-tenta o enroll sozinha a cada 1 h, entao
    corrigir o `install.json` ou rodar o enroll manual ja resolve.
  - Para 422, aumente o limite do plano ou arquive/remova devices nao usados no portal e deixe o
    re-enroll seguinte passar.

### 4.2 Sem conectividade / firewall bloqueia a saida HTTPS

- **Sintoma**
  - Estacao: "Status da conexao" mostra "Servidor: servidor inacessivel (sem rede) - eventos
    preservados na fila local".
  - Log do servico: "Servidor inacessivel - eventos permanecem na fila local (N pendentes). Nova
    tentativa com backoff".
  - Portal: o device fica "Sem comunicacao" (sem contato ha mais de 180 s; realce se passar de
    30 min em horario de trabalho).
- **Causa**: DNS nao resolve o host do `SERVERURL`, conexao recusada, timeout, ou firewall/regra
  de saida bloqueando HTTPS para o servidor.
- **Acao**
  - Da propria estacao, valide a saida HTTPS para o `SERVERURL` (ex.: abrir a URL base no
    navegador ou `Invoke-WebRequest`). Confira a resolucao DNS do host.
  - Libere a saida HTTPS para o host do servidor na regra de firewall/proxy.
  - Nao ha perda de dados nesse estado: os eventos ficam na fila local (SQLite WAL) e sao enviados
    assim que a conexao volta.

### 4.3 Proxy corporativo

- **Sintoma**: igual ao item 4.2 ("servidor inacessivel"), mas apenas nas estacoes atras de proxy,
  enquanto maquinas em rede direta conectam normalmente.
- **Causa**: o proxy de sistema (WinHTTP) nao esta configurado para o contexto do servico
  (LocalSystem), ou o trafego HTTPS so sai pelo proxy e o agente nao o esta usando.
- **Acao**
  - Reinstale (ou ajuste) informando o proxy explicito:
    ```
    msiexec /i MonitorAgent.msi /qn ENROLLKEY=ek_XXXX SERVERURL=https://api.seu-dominio.com.br PROXYURL=http://proxy.interno:8080
    ```
  - O `PROXYURL` e gravado no `install.json` e usado por enroll, envio e auto-update. O log do
    servico confirma: "Proxy: usando PROXYURL do instalador (...)". Sem `PROXYURL` o log mostra
    "Proxy: usando proxy de sistema (WinHTTP) ou conexao direta".
  - Limitacao desta versao: o proxy usa as credenciais padrao do contexto; proxy que exige
    usuario/senha explicitos nao e suportado. Nesses casos, libere o host do servidor como bypass
    de autenticacao no proxy.

### 4.4 Inspecao TLS / MITM (erro de certificado)

- **Sintoma**
  - Estacao: "Status da conexao" mostra "Servidor: erro de certificado (possivel inspecao de
    rede/MITM) - verifique com o TI", com icone de aviso.
  - Log do servico: "Erro de certificado TLS ao falar com o servidor (cadeia invalida ou possivel
    inspecao MITM). Eventos preservados na fila (...); validacao de certificado NAO foi
    desabilitada."
  - Portal: o device fica "Sem comunicacao" (o agente nunca estabelece conexao).
- **Causa**: um appliance de inspecao TLS (firewall/proxy com SSL inspection) reescreve o
  certificado do servidor com uma CA que a estacao nao confia para esse host, ou a cadeia do
  servidor esta incompleta/invalida na estacao. O agente recusa o handshake de proposito e nunca
  desabilita a validacao.
- **Acao**
  - Adicione o host do `SERVERURL` a lista de bypass de inspecao TLS do appliance (recomendado),
    ou
  - Garanta que a CA do appliance de inspecao esteja confiavel no armazenamento de certificados da
    maquina (LocalMachine) para que a cadeia valide. O agente usa o armazenamento da maquina, nao
    o do usuario.

### 4.5 Antivirus ou usuario mata o helper (watchdog e adulteracao)

- **Sintoma**
  - O processo `MonitorAgentSession.exe` desaparece e reaparece; o icone da bandeja some e volta.
  - Portal: o device sinaliza "Adulteracao detectada" (evento `AGENT_TAMPER`) e ha um gap visivel
    na linha do tempo daquela sessao. Atencao: **um unico kill ja marca "Adulteracao detectada"** -
    o watchdog emite um `AGENT_TAMPER` a cada relancamento, nao so quando vira "repetido".
- **Causa**: o helper foi encerrado (antivirus/EDR classificando errado, usuario matando pelo
  Gerenciador de Tarefas, ou o canal de comunicacao foi bloqueado). O servico tem um watchdog que
  trata isso assim:
  - **Cada encerramento** gera um `AGENT_TAMPER` com motivo `helper_killed` ("Helper encerrado") e
    o helper e religado em **5 s**.
  - **Acima de 5 relancamentos em 10 minutos** o motivo passa a `helper_killed_repeatedly` ("Helper
    encerrado repetidamente") e o religamento espaca para **a cada 15 minutos**.
  - Se o canal (named pipe) for bloqueado, o motivo e `pipe_denied` ("Acesso ao canal negado").
  Em todos os casos a ocorrencia fica registrada e visivel no portal.
- **Acao**
  - Adicione exclusao no antivirus/EDR para `%ProgramFiles%\M351\MonitorAgent\` (ambos os exes) e
    para o servico `MonitorAgentService`. Confirme que o EDR nao esta bloqueando o
    `CreateProcessAsUser` do servico nem o named pipe local.
  - Se foi acao de usuario, isso e esperado e fica registrado: o watchdog religa o helper e o gap
    + tamper ficam visiveis no portal (transparencia por design; nao ha como ocultar).

### 4.6 Relogio dessincronizado (estacao com hora fora do ar)

- **Sintoma**
  - Portal: o device fica com "Relogio dessincronizado" no painel de saude.
  - Eventos individuais com timestamp muito no futuro podem ser rejeitados pelo servidor (o lote
    nao trava; so os eventos fora da janela sao recusados).
  - Log do servico pode registrar "Mudanca de relogio detectada (...) - TIME_CHANGED emitido".
- **Causa**: o relogio da estacao esta com desvio relevante (o portal sinaliza quando o desvio
  medido passa de 2 minutos). Causas comuns: sincronizacao de hora desativada, fuso/horario
  errado, ou relogio adulterado pelo usuario.
- **Acao**: garanta a sincronizacao de hora (servico Hora do Windows / NTP / controlador de
  dominio) e o fuso correto. O servidor corrige a ordenacao internamente por relogio monotonico,
  mas a sincronizacao evita o alerta e a rejeicao de eventos com hora futura.

---

## 5. Pacote de suporte (--diag)

Quando precisar abrir chamado, gere o ZIP de suporte na estacao. Ele contem os logs ja
sanitizados (sem token, sem titulos de janela, sem usuario) e um resumo da maquina, seguro para
enviar ao suporte.

Como gerar (na sessao do usuario afetado, no diretorio de binarios):

```
"%ProgramFiles%\M351\MonitorAgent\MonitorAgentSession.exe" --diag
```

O comando nao abre janela: ele escreve o caminho do ZIP gerado na saida padrao. O arquivo fica em:

```
%TEMP%\monitoragent-diag-AAAAMMDD-HHMMSS.zip
```

Conteudo do ZIP:

- `logs/` - todos os `*.log` do agente, com cada linha passada por um redator que remove dados
  sensiveis (titulo de janela, usuario, caminhos, token). Mesmo logs em nivel Debug
  (`verbose_debug`) saem sanitizados.
- `info.txt` - versao do agente, data/hora de geracao e nome da maquina.

Anexe esse ZIP ao chamado. Nao e necessario coletar nada manualmente da pasta de logs; o `--diag`
ja faz a sanitizacao.

> O nivel de log Debug (`verbose_debug` no `install.json`) so deve ser ligado durante diagnostico
> orientado pelo suporte: nele detalhes como titulo de janela e usuario podem aparecer nos
> arquivos de log locais. O `--diag` continua sanitizando esses dados ao empacotar; ainda assim,
> desligue o `verbose_debug` apos o diagnostico.

---

## Referencia rapida de estados (estacao e portal)

| Situacao | "Status da conexao" na bandeja | Painel de saude no portal |
|---|---|---|
| Tudo certo | conectado ao servidor | sem alerta |
| Nao registrado | dispositivo ainda nao registrado | Sem comunicacao (nao aparece ou sem contato) |
| Sem rede / firewall | servidor inacessivel (sem rede) | Sem comunicacao |
| Inspecao TLS/MITM | erro de certificado (possivel inspecao de rede/MITM) | Sem comunicacao |
| Helper morto (qualquer kill) | (canal local pode oscilar) | Adulteracao detectada |
| Hora fora de sincronia | (sem mudanca no status) | Relogio dessincronizado |
| Versao antiga | (sem mudanca no status) | Versao desatualizada |
| Aviso de coleta nao confirmado | (sem mudanca no status) | Ciencia pendente |
