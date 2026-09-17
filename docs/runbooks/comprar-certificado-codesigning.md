# Guia — Comprar o certificado de code signing (Authenticode)

> Pré-requisito da F5 e do gate de GA. O gancho de assinatura já existe em
> `agent/installer/build-agent-msi.ps1` (vars `SIGN_THUMBPRINT`/`SIGN_PFX`); este guia
> é para você (Joao) adquirir o certificado. **Lead time real: 1 a 3 semanas** por causa
> da validação da empresa — começar cedo.

## O que a assinatura REALMENTE faz sumir (leia antes de comprar)

Três telas diferentes do Windows são confundidas como "o aviso". Só uma some no dia 1:

| Tela | Texto que o cliente vê | Assinar resolve? |
|---|---|---|
| **UAC** (o prompt do duplo clique) | "Editor desconhecido" → vira "Editor verificado: RAZÃO SOCIAL" | ✅ **Sim, imediatamente**, no primeiro MSI assinado |
| **SmartScreen** | "O Windows protegeu o seu PC — aplicativo não reconhecido" | ⚠️ **Não automaticamente.** Depende de REPUTAÇÃO, que se acumula com downloads/instalações ao longo do tempo |
| **Smart App Control** | "O Controlo Inteligente de Aplicações bloqueou parte desta aplicação" | ⚠️ **Não automaticamente**, mesma história de reputação — mas sem assinatura é bloqueio garantido |

Ou seja: **"Editor desconhecido" some; "aplicativo não reconhecido" não some sozinho.** O que a
assinatura muda no SmartScreen/SAC é que a reputação passa a ser acumulada **na identidade do
certificado** (todo binário que você assinar herda o histórico) em vez de por hash de arquivo —
sem certificado, cada build novo recomeça do zero e nunca sai do aviso.

**EV não compra atalho.** A reputação instantânea que o EV dava foi descontinuada pela Microsoft;
a própria DigiCert publica que o EV não garante mais ausência de aviso. Por isso a recomendação
abaixo é OV: o EV custa o dobro e chega no mesmo lugar.

## Por que precisamos

Sem assinatura Authenticode, o Windows SmartScreen/Defender mostra "Editor desconhecido" e
pode bloquear o MSI; instaladores não assinados queimam a confiança do cliente logo no primeiro
contato com o TI. A spec (Seção 6.6) exige MSI assinado.

### Smart App Control: o bloqueio duro (confirmado em campo, 22/08/2026)

> **Incidente real.** Instalação numa máquina com Windows 11 devolveu "O Controlo Inteligente de
> Aplicações bloqueou parte desta aplicação". O MSI passou, os dois executáveis
> (`MonitorAgentService.exe` e `MonitorAgentSession.exe`) foram barrados. Confirmado com
> `Get-MpComputerStatus | Select-Object SmartAppControlState` devolvendo `On`.

O Smart App Control (SAC, "Controlo Inteligente de Aplicações" em pt-PT) NÃO é o SmartScreen.

| | SmartScreen | Smart App Control |
|---|---|---|
| O que é | Aviso na tela, no duplo clique | Integridade de código, aplicada abaixo |
| Instalação silenciosa escapa? | Sim | **Não.** Não importa se foi `/qn` |
| Usuário pode liberar o app? | Sim, "Executar assim mesmo" | **Não existe exceção por aplicativo** |
| Como desligar | Configurável | **Porta de mão única**, só volta reinstalando o Windows |

Quando o SAC está ligado, binário não assinado simplesmente não executa. Não há contorno do lado
do fabricante: nem certificado auto-assinado, nem raiz confiável adicionada na máquina, nem
política local. É assinar e ganhar reputação, ou o cliente desligar o SAC, o que NUNCA se deve
pedir a um cliente (é pedir para baixar a guarda do sistema para instalar um software de
monitoramento, exatamente o oposto do que a marca vende).

**Alcance real do problema:** o SAC só liga sozinho em INSTALAÇÃO LIMPA do Windows 11 22H2 ou
superior. Máquina que veio de upgrade do Windows 10, ou reimageada com imagem corporativa, fica
com ele desligado. Antes de tratar isso como emergência, meça: peça ao TI do prospecto para rodar
`Get-MpComputerStatus | Select-Object SmartAppControlState` em algumas máquinas da frota.

## Quando comprar — assim que houver data de piloto

> **CORREÇÃO de 22/08/2026.** Este runbook dizia "NÃO agora" com a justificativa de que
> "o `msiexec /qn` instala sem prompt". Isso vale para o SmartScreen e **é falso para o Smart App
> Control**, como o incidente acima provou. Em máquina com SAC ligado, a instalação silenciosa
> também é bloqueada, e o agente não roda.

O certificado continua sendo gate do piloto, não do desenvolvimento, mas o prazo mudou de figura:
como a validação leva de 1 a 3 semanas E a reputação só se acumula depois, com instalações reais,
**comprar no dia em que a data do piloto for marcada já é tarde**. Compre assim que a data existir
no horizonte.

Para desenvolvimento e teste interno, use máquina com SAC desligado. Em VM descartável, desligar
o SAC não custa nada, porque a VM se recria. Nunca queime o interruptor de uma máquina real: ele
não tem volta.

## Validade máxima agora é 460 dias (CA/B Forum, ballot CSC-31)

Desde **01/03/2026** nenhum certificado público de code signing pode ser emitido com validade
maior que **460 dias** (~15 meses). O teto anterior era 39 meses.

O que isso muda na prática: **plano plurianual não é mais "compra e esquece"**. Um plano de 3 anos
continua valendo a pena pelo desconto, mas significa "assinatura de 3 anos com **reemissão a cada
~15 meses**" — e cada reemissão refaz a validação da empresa. Consequências para nós:

- coloque o vencimento no calendário **com 60 dias de antecedência**; certificado vencido não
  invalida o que já foi assinado (por causa do timestamp RFC 3161 que o nosso `signtool` já usa),
  mas trava o release seguinte;
- se a razão social mudar entre reemissões, o `expected_signer_cn` do `install.json` tem de mudar
  no mesmo release (ver seção adiante).

## Opções por custo (cotações de 17/09/2026 — confirmar no momento da compra)

| Opção | Custo | Chave | Assina no nosso CI? | Observações |
|---|---|---|---|---|
| ~~**Azure Trusted Signing**~~ | ~~US$ 9,99/mês~~ | Cloud | Sim | ❌ **INDISPONÍVEL NO BRASIL** — a lista de países suportados (GA de abril/2026) não inclui o Brasil. De longe o mais barato; vale re-checar daqui a uns 6 meses. |
| **SSL.com OV + eSigner** | **~US$ 309/ano** (cert ~US$ 129 + eSigner tier 1 ~US$ 180/ano, 20 assinaturas/mês; 30 dias grátis) | Cloud HSM | ✅ **Sim, com Action oficial** (`SSLcom/esigner-codesign`) | Mais caro que o Certum, mas é o único que pluga direto no `publicar-release-agente.yml` sem gambiarra. |
| **Certum Cloud Code Signing (OV)** | **US$ 177/ano (1 ano), US$ 132/ano (2 anos), US$ 116/ano (3 anos)** | Cloud (SimplySign, sem token) | ⚠️ Difícil (exige SimplySign Desktop + OTP no runner) | **O mais barato.** Bom se a assinatura for manual, na sua máquina. |
| **Sectigo Brasil (direto)** | **R$ 3.026/ano OV, R$ 3.585/ano EV** | Token USB FIPS | ❌ Não | Nota fiscal e suporte em pt-BR, validação pelo CNPJ. Caro, e o token não entra no runner do GitHub. |
| **Revendedor BR (rapidssl.com.br)** | **R$ 1.419 OV ECC / R$ 1.999 OV RSA / R$ 2.189 EV ECC** | Token USB enviado pelo correio | ❌ Não | Mais barato que a Sectigo Brasil, mas token importado = risco de alfândega + assinatura manual. |
| **DigiCert direto** | ~€850/ano | Cloud (KeyLocker) | Sim | Premium — caro demais para a nossa fase, sem vantagem funcional. |

### O critério que decide: o token USB NÃO funciona no nosso release

O `publicar-release-agente.yml` constrói o MSI num runner **`windows-latest` hospedado pelo
GitHub** — máquina efêmera, na nuvem, sem porta USB nossa. Qualquer opção com token físico
significa **quebrar o release de um clique**: baixar o MSI como artifact, assinar na sua máquina
com o token plugado e reenviar por um caminho novo que ainda não existe.

Por isso as opções em BRL com token (Sectigo Brasil, rapidssl.com.br), apesar de darem nota
fiscal em real, **ficam de fora** enquanto o release for automatizado. Some a isso o risco
concreto de o token travar na alfândega.

## DECISÃO (17/09/2026): SSL.com OV + eSigner

Escolhido. O que pesou: é cloud (sem token, sem alfândega), tem Action oficial de GitHub
(`SSLcom/esigner-codesign`) e por isso o release continua sendo um clique. Custa ~US$ 190/ano a
mais que o Certum — exatamente o preço de não ter trabalho manual em todo release.

**Atende empresa brasileira, confirmado na política da própria CA:** a lista de países restritos
da SSL.com tem quatro nomes (Cuba, Irã, Coreia do Norte, Síria). BR é país aceito, não há
bloqueio de exportação. O produto é global; o que varia por país é a *forma de comprovar a
empresa*, resolvida na seção abaixo.

### Como preencher o pedido

Comece pela página do certificado, não pela do eSigner: `ssl.com/products/software-integrity/code-signing/ov/`
→ **Configure & Buy**. O formulário tem 3 passos:

1. **Certificado:** OV Code Signing — US$ 129/ano (1 ano) ou **US$ 109,65/ano no plano de 3 anos**.
   NÃO pegue EV (US$ 349/ano): não resolve nada mais rápido (ver seção OV vs EV).
2. **Key Storage & Delivery:** **eSigner Cloud Signing, Tier 1** — US$ 15/mês na cobrança anual
   (US$ 180/ano, 240 assinaturas/ano; sobra muito para nós). **NÃO** escolha *YubiKey* (+US$ 249):
   é o token físico, que não funciona no runner do GitHub. Cloud HSM próprio (a partir de US$ 500
   mais taxa de atestação de US$ 500–1.500) é overkill para a nossa escala.
3. **Validation Speed:** **Standard** (incluída). *Expedited* custa **+US$ 599** para adiantar
   1–3 dias — não vale.

Total do primeiro ano: **~US$ 309** (~US$ 290/ano no plano de 3 anos). Os primeiros **30 dias de
eSigner são grátis**: dá para provar a assinatura no CI antes de a mensalidade começar.

### O que a SSL.com vai pedir para validar a empresa brasileira

A validação aceita **link para um órgão de governo da jurisdição onde a empresa foi constituída**
— no nosso caso, o **Comprovante de Inscrição e de Situação Cadastral do CNPJ na Receita
Federal**. É o caminho principal e não exige nada exótico; costuma bastar mandar o link para
`support@ssl.com`.

- **D-U-N-S acelera** — a SSL.com diz isso explicitamente. É gratuito; se a empresa ainda não tem,
  peça AGORA, porque criar ou atualizar cadastro leva dias e é o gargalo típico.
- Bases de terceiros também servem: OpenCorporates, ZoomInfo, Crunchbase, Bloomberg, D&B.
- **Empresa com menos de 3 anos: exigem scan de documento com foto do solicitante.** Deixe pronto.
- **A ligação de confirmação acontece DEPOIS de a validação ser aprovada**, não durante — mas o
  telefone da empresa ainda precisa estar localizável.

### Alternativas descartadas

- **Certum Cloud OV, 3 anos (US$ 348 total, ~US$ 116/ano)** — o mais barato, e o plano B se o
  orçamento apertar. Custo escondido: assinatura manual, porque o SimplySign exige app desktop +
  OTP; nesse cenário o `publicar-release-agente.yml` precisa ganhar uma entrada para MSI já
  assinado.
- **Azure Trusted Signing** — não atende o Brasil.
- **EV, em qualquer CA** — o dobro do preço sem resolver SmartScreen nem SAC mais rápido.
- **Sectigo Brasil / revendedores em BRL** — só se nota fiscal em real pesar mais que a automação
  do release, porque todos entregam em token USB.

## OV vs EV — qual comprar

| | **OV (Organization Validation)** | **EV (Extended Validation)** |
|---|---|---|
| Custo aproximado | ~US$ 116–310/ano | ~US$ 226–900/ano |
| Reputação SmartScreen | Ganha com o tempo/volume de instalações | Ganha com o tempo também. **A vantagem de reputação imediata do EV ACABOU**, a própria DigiCert publica que EV não garante mais ausência de aviso |
| Armazenamento da chave | HSM/token FIPS (obrigatório desde jun/2023) | HSM/token FIPS (sempre foi) |
| Validação | Identidade da empresa (CNPJ etc.) | Identidade + verificação reforçada |

**Recomendação para o MVP: OV, e por um motivo diferente do que estava escrito aqui antes.**

O raciocínio antigo era "OV basta porque instalamos em silêncio e o silêncio escapa do
SmartScreen". Esse raciocínio caiu com o incidente do SAC de 22/08/2026. O motivo válido hoje é
outro: **o EV custa o dobro e não resolve o SAC**, porque a reputação imediata do EV deixou de
existir. Nem OV nem EV fazem o SAC liberar no dia 1, os dois precisam acumular reputação. Logo,
pague o mais barato.

O que de fato acelera a reputação, e vale mais do que a diferença entre OV e EV:

1. assinar TODOS os binários, os dois exes e o MSI, não apenas o instalador;
2. submeter cada versão relevante ao Microsoft Security Intelligence (seção adiante);
3. volume de instalações limpas ao longo do tempo, que é justamente o que não se compra.

## O que os concorrentes fazem (pesquisa de 17/09/2026)

Assinar é o padrão entre os concorrentes que vendem para TI corporativo — e quem não assina
empurra o custo para o cliente, na forma de lista de exclusão no antivírus.

| Concorrente | Assina? | Evidência |
|---|---|---|
| **Teramind** | ✅ Sim, **EV** | Base de conhecimento própria: agente e drivers assinados; certificados emitidos pela DigiCert EV Code Signing CA (driver em modo kernel exige EV) |
| **Hubstaff** | ✅ Sim | Oferece "code-signed MSI installer" como recurso de deploy para TI |
| **ActivTrak** | ⚠️ Documenta o aviso | O próprio help center ensina o cliente a passar pelo "Windows protected your PC" do SmartScreen — prova de que nem gigante do setor escapa da reputação |
| **Kickidler** | ❔ Sem evidência pública de assinatura | Em vez disso publica uma página de "Kickidler e antivírus" com instruções de exclusão, e usa print de VirusTotal limpo como argumento |
| **Monitoo** (BR) | ❔ Sem evidência pública de assinatura | **Distribui o `Monitoo-Installer.MSI` pelo Google Drive**, e a página de instalação não menciona editor nem certificado |

Leituras para nós:

1. **Assinar é preço de entrada** para conversa com TI corporativo — é isso que o Teramind e o
   Hubstaff colocam no material de vendas.
2. **O ActivTrak confirma a parte chata**: mesmo assinado e com anos de mercado, ainda se
   documenta o aviso do SmartScreen. Nossa expectativa tem de ser "some o 'Editor desconhecido' e
   o aviso vai diminuindo", nunca "acabou o atrito no dia 1".
3. **O caminho do Kickidler (não assinar e pedir exclusão) é vendável, mas caro na relação**: numa
   ferramenta de monitoramento, pedir para o TI baixar a guarda do antivírus é péssimo argumento.
4. **O Google Drive da Monitoo é o oposto do que assinar resolve**: baixar de lá carimba o arquivo
   com Mark of the Web de origem externa e ainda pega o aviso do próprio Drive para arquivo grande
   não verificado. Quem tem certificado e reputação hospeda no domínio próprio — e isso vira
   argumento de venda contra eles.

### PENDÊNCIA ABERTA — confirmar assinatura dos concorrentes (decidido em 17/09/2026)

A tabela acima é **indício público**, não prova. A prova é de 10 segundos por instalador:

```powershell
Get-AuthenticodeSignature "C:\caminho\Instalador.msi" |
  Format-List Status, StatusMessage, SignerCertificate
```

`Status: NotSigned` encerra a discussão. `Valid` mostra a razão social e a CA no
`SignerCertificate` — que é como sabemos, por exemplo, que o Teramind usa DigiCert EV.

Alvos, em ordem de valor comercial: **Monitoo** (Google Drive, link na página de instalação
deles), **Kickidler**, **ActivTrak**, **Hubstaff**.

> ⚠️ **NÃO baixe isso na máquina corporativa.** São agentes de monitoramento; o EDR da empresa
> pode barrar o download e abrir um incidente de segurança em nome de quem baixou. Faça numa VM
> descartável — a mesma que já usamos para testar Smart App Control.

O resultado entra na tabela acima, trocando ❔ por ✅/❌ com a CA emissora.

## Onde comprar (CAs e revendedores)

- **SSL.com** — cloud signing (eSigner) com Action oficial de GitHub; a opção recomendada aqui.
- **Certum** (via revendedores como SSLmentor) — cloud signing SimplySign, o mais barato.
- **Sectigo Brasil** (sectigo.com.br) e revendedores BR (rapidssl.com.br, meussl.com.br) — faturam
  em R$, mas entregam em token USB.
- **DigiCert** (direto) — premium, integra com KeyLocker (assinatura na nuvem).
- Não confundir com certificado ICP-Brasil/e-CNPJ: Authenticode é outro produto, padrão global da
  CA/B Forum. e-CNPJ **não** assina software para o Windows.

## O que a CA vai exigir da empresa (prepare antes)

- **CNPJ ativo** e razão social que bata com o nome que aparecerá no certificado (= o "Editor"
  mostrado no Windows). Decida com que nome o produto deve assinar.
- Comprovante de endereço da empresa / presença em diretório (às vezes pedem registro
  D-U-N-S — gratuito, mas pode levar dias para criar/atualizar).
- **Verificação por telefone**: a CA liga para um número público da empresa (listado em diretório
  oficial) para confirmar o pedido — garanta que o telefone da empresa esteja localizável.
- E-mail corporativo no domínio da empresa.
- Documento de identidade com foto do representante legal, e procuração se quem pedir não for ele.

## Armazenamento da chave (decisão técnica importante)

Desde jun/2023 a CA/B Forum exige que a chave privada de code signing fique em **hardware FIPS
140-2** (token USB ou HSM na nuvem) — **não existe mais .pfx baixável** para OV/EV novos. Opções:

1. **Token USB físico** (vem da CA): assina na máquina onde o token está plugado. Simples, mas
   **não automatiza no CI** (o token tem que estar presente) — e o nosso CI roda em runner
   hospedado. Só serve se a assinatura for manual.
2. **Cloud signing / HSM gerenciado** (SSL.com eSigner, DigiCert KeyLocker, Azure Trusted Signing,
   SignPath): a chave vive num HSM na nuvem; o build assina via API. **Permite assinar no CI**
   (jobs `agent-msi` do CI e `msi` do `publicar-release-agente.yml`). É o caminho para automatizar.

**Recomendação:** cloud signing. O token só entra em cena se a compra for em BRL com nota fiscal
brasileira, e aí o release deixa de ser de um clique.

## Como ligar no nosso build (quando o cert chegar)

O `build-agent-msi.ps1` já tem o gancho condicional. Ajuste conforme a opção:
- **Token/pfx local:** definir `SIGN_THUMBPRINT` (thumbprint do cert no store) ou `SIGN_PFX` — o
  script chama `signtool sign /sha1 <thumb> /tr <timestamp-RFC3161> /td sha256 /fd sha256` nos
  2 exes e no MSI.
- **Cloud signing:** trocar a chamada `signtool` pelo CLI/Action do provedor (no SSL.com, a Action
  `SSLcom/esigner-codesign`; no Azure, `azuresigntool`) com as credenciais do HSM em secrets do
  GitHub Actions. Me peça que eu adapto o gancho quando você souber qual provedor.

## PASSO OBRIGATÓRIO no release pós-compra: ligar a verificação no agente

Assinar o MSI resolve o SmartScreen, mas **não** protege o auto-update sozinho: o agente já
instalado precisa RECUSAR um MSI que não esteja assinado por você. Essa verificação existe no
código (`UpdateInstaller.VerifyAuthenticode` → `Authenticode.Verify`, com `WinVerifyTrust`), mas
nasce **desligada** por uma flag, justamente porque hoje o MSI não é assinado e o agente precisa
conseguir se atualizar em dev/piloto interno.

No **primeiro release empacotado com o certificado**, ligue a flag no `install.json`:

```json
{
  "server_url": "https://api.produto.com.br",
  "verify_authenticode": true,
  "expected_signer_cn": "RAZAO SOCIAL EXATA DO CERTIFICADO"
}
```

Na prática, quem grava o arquivo é a custom action do MSI, então basta passar os argumentos novos:

```
MonitorAgentService.exe --write-install-config --data-dir "%ProgramData%\M351\MonitorAgent" ^
    --server https://api.produto.com.br ^
    --verify-authenticode 1 ^
    --expected-signer-cn "RAZAO SOCIAL EXATA DO CERTIFICADO"
```

Regras e cuidados:
- `expected_signer_cn` é **opcional, mas recomendado**: sem ele, qualquer MSI assinado por um
  certificado confiável do sistema passa. Com ele, um MSI assinado por OUTRA empresa é recusado.
  O valor tem de ser um trecho do `Subject` do certificado (a razão social como ela aparece no
  campo `CN=` — confira com `signtool verify /pa /v MonitorAgent.msi` depois de assinar).
- **Ordem correta do rollout:** publique primeiro um release ASSINADO com a flag ainda desligada
  (assim toda a frota chega numa versão assinada) e só no release SEGUINTE ligue a flag. Ligar a
  flag antes de a frota estar em versão assinada não quebra nada hoje (a verificação é do MSI
  BAIXADO, não do instalado), mas essa ordem deixa um degrau a menos de risco.
- Comportamento com a flag ligada: MSI sem assinatura, com certificado revogado/expirado, com
  cadeia incompleta ou assinado por outro titular é **descartado sem instalar** (mesma disciplina
  do SHA-256 divergente), com o motivo exato no log do serviço. O update é retentado no ciclo
  seguinte (6 h), então um erro de configuração aqui atrasa updates — não derruba o agente.
- Renovação do certificado: com o teto de 460 dias, isso acontece a cada ~15 meses. Se a razão
  social no `CN=` mudar, atualize `expected_signer_cn` no mesmo release em que o certificado novo
  passa a assinar.

## Depois de assinar: submeter ao Microsoft Defender

Antes de instalar em cliente, submeter o MSI assinado ao **Microsoft Security Intelligence**
(https://www.microsoft.com/wdsi/filesubmission) como software legítimo, para evitar falso-positivo
inicial do Defender. Re-submeter a cada versão nova relevante.

## Próximo passo

Fornecedor já decidido (SSL.com OV + eSigner). Falta:

1. Decidir o nome jurídico do "Editor" (= razão social no certificado), porque é ele que aparece
   no UAC no lugar de "Editor desconhecido". **Isso trava o pedido — decida primeiro.**
2. Providenciar o **D-U-N-S** (gratuito, leva dias) e separar o comprovante de CNPJ da Receita
   Federal + documento com foto do solicitante.
3. Comprar com as escolhas da seção "Como preencher o pedido" (OV, eSigner Tier 1, Standard) e
   iniciar a validação — é o que demora: 1 a 3 semanas.
4. Quando o eSigner estiver ativo, me passar usuário, senha, segredo TOTP e *credential ID* para
   virarem secrets do GitHub; eu ligo a Action `SSLcom/esigner-codesign` no `build-agent-msi.ps1`,
   no job de CI e no `publicar-release-agente.yml`.
5. Primeiro release assinado com `verify_authenticode` ainda **desligado**; ligar só no seguinte
   (ver "PASSO OBRIGATÓRIO" acima).
6. Agendar o vencimento (460 dias) no calendário, com alarme 60 dias antes.
7. Rodar a checagem de assinatura dos concorrentes (ver "PENDÊNCIA ABERTA" acima).
