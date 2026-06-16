# Guia — Comprar o certificado de code signing (Authenticode)

> Pré-requisito da F5 e do gate de GA. O gancho de assinatura já existe em
> `agent/installer/build-agent-msi.ps1` (vars `SIGN_THUMBPRINT`/`SIGN_PFX`); este guia
> é para você (Joao) adquirir o certificado. **Lead time real: 1 a 3 semanas** por causa
> da validação da empresa — começar cedo.

## Por que precisamos

Sem assinatura Authenticode, o Windows SmartScreen/Defender mostra "Editor desconhecido" e
pode bloquear o MSI; instaladores não assinados queimam a confiança do cliente logo no primeiro
contato com o TI. A spec (Seção 6.6) exige MSI assinado.

## Quando comprar — NÃO agora

O certificado é **gate da F5/piloto** (instalar em cliente real), **não do desenvolvimento**.
Durante o dev e testes internos (VMs, PC-CASA, demos), o MSI **não-assinado funciona**: o
`msiexec /qn` instala sem prompt; o aviso do SmartScreen só aparece em duplo-clique manual e
você mesmo aprova. **Não gaste enquanto está só desenvolvendo** — compre quando tiver data de
piloto marcada.

## Opções por custo (preços de 2026 — confirmar no momento da compra)

| Opção | Custo | Modelo | Observações |
|---|---|---|---|
| **Azure Trusted Signing** (renomeado "Azure Artifact Signing" em jan/2026) | **~US$ 9,99/mês** (até 5.000 assinaturas) | **Mensal — paga só quando usa** | Mais barato e moderno; reputação SmartScreen imediata (é Microsoft); HSM gerenciado embutido; assina no CI via `azuresigntool`. **Pegadinha de elegibilidade** (ver abaixo). |
| **Certum Cloud Code Signing** | **~US$ 108/ano** (~€100) | Anual, cloud (sem token físico) | Mais barato em modelo anual; CA reconhecida. |
| **Sectigo OV** via revendedor (SSL2BUY, SignMyCode, CheapSSLShop) | **~US$ 215–226/ano** (~€200) | Anual, token HSM | Padrão de mercado; mais barato que comprar da CA direto. |
| **DigiCert direto** | **~€71/mês (~€850/ano)** | Anual | Premium — caro demais para a sua fase, sem vantagem funcional sobre os acima. |

### Recomendação para você (PT, fase de dev, custo sensível)

1. **Agora:** não compre. Siga com MSI não-assinado.
2. **No piloto:** **Azure Trusted Signing (~€9/mês)** é a melhor escolha — é mensal (você pausa
   quando não precisa, em vez de €71/mês fixos), tem reputação SmartScreen imediata e assina no
   CI. Cobre **organizações na UE** (a empresa `+351`/PT qualifica geograficamente).
3. **Se a empresa tiver < 3 anos** (a Azure exige org com ≥ 3 anos para validação "Organization"):
   ou usar a **validação "Individual"** da Azure (estava em rollout em 2025 — confirmar se já
   abriu para PT) **ou** ir de **Certum Cloud (~€100/ano)**, que não tem esse requisito de idade.

## OV vs EV — qual comprar (se for de certificado tradicional, não Azure)

| | **OV (Organization Validation)** | **EV (Extended Validation)** |
|---|---|---|
| Custo aproximado | ~US$ 200–400/ano | ~US$ 400–700/ano |
| Reputação SmartScreen | Ganha com o tempo/volume de instalações | **Imediata** (sem aviso desde a 1ª instalação) |
| Armazenamento da chave | HSM/token FIPS (obrigatório desde jun/2023) | HSM/token FIPS (sempre foi) |
| Validação | Identidade da empresa (CNPJ etc.) | Identidade + verificação reforçada |

**Recomendação para o MVP:** **OV é suficiente.** Nossa distribuição é MSI **silencioso via
GPO/Intune/RMM** (`msiexec /qn`), que **não** dispara o prompt interativo do SmartScreen — o
aviso de reputação afeta sobretudo download manual + duplo-clique. Se no futuro você distribuir
o MSI por download direto no site, aí o EV elimina o atrito imediatamente. Começar com OV e
migrar para EV depois é um caminho normal.

## Onde comprar (CAs e revendedores)

- **DigiCert** (direto) — premium, suporte bom, integra com KeyLocker (assinatura na nuvem).
- **Sectigo** (via revendedores: SSL.com, The SSL Store, Certera, GoGetSSL) — mais barato.
- **SSL.com** — costuma ter cloud signing (eSigner) que evita o token físico.
- No Brasil há revendedores que faturam em BRL; a CA em si é internacional (não confundir com
  certificado ICP-Brasil/e-CNPJ — Authenticode é outro produto, padrão global da CA/B Forum).

## O que a CA vai exigir da empresa (prepare antes)

- **CNPJ ativo** e razão social que bata com o nome que aparecerá no certificado (= o "Editor"
  mostrado no Windows). Decida com que nome o produto deve assinar.
- Comprovante de endereço da empresa / presença em diretório (às vezes pedem registro
  D-U-N-S — gratuito, mas pode levar dias para criar/atualizar).
- **Verificação por telefone**: a CA liga para um número público da empresa (listado em diretório
  oficial) para confirmar o pedido — garanta que o telefone da empresa esteja localizável.
- E-mail corporativo no domínio da empresa.

## Armazenamento da chave (decisão técnica importante)

Desde jun/2023 a CA/B Forum exige que a chave privada de code signing fique em **hardware FIPS
140-2** (token USB ou HSM na nuvem) — **não existe mais .pfx baixável** para OV/EV novos. Opções:

1. **Token USB físico** (vem da CA): assina na máquina onde o token está plugado. Simples, mas
   **não automatiza no CI** (o token tem que estar presente). Bom para começar assinando manualmente.
2. **Cloud signing / HSM gerenciado** (DigiCert KeyLocker, SSL.com eSigner, Azure Trusted Signing,
   SignPath): a chave vive num HSM na nuvem; o build assina via API. **Permite assinar no CI**
   (o job `agent-msi` do GitHub Actions). É o caminho para automatizar.

**Recomendação:** se o objetivo é assinar no CI, escolher uma CA com **cloud signing** (ex.:
Azure Trusted Signing é barato e integra com `signtool`/`azuresigntool`). Se for assinar
manualmente no começo, o token USB resolve.

## Como ligar no nosso build (quando o cert chegar)

O `build-agent-msi.ps1` já tem o gancho condicional. Ajuste conforme a opção:
- **Token/pfx local:** definir `SIGN_THUMBPRINT` (thumbprint do cert no store) ou `SIGN_PFX` — o
  script chama `signtool sign /sha1 <thumb> /tr <timestamp-RFC3161> /td sha256 /fd sha256` nos
  2 exes e no MSI.
- **Cloud signing:** trocar a chamada `signtool` por `azuresigntool` (ou o CLI do provedor) com as
  credenciais do HSM em secrets do GitHub Actions. Me peça que eu adapto o gancho quando você
  souber qual provedor.

## Depois de assinar: submeter ao Microsoft Defender

Antes de instalar em cliente, submeter o MSI assinado ao **Microsoft Security Intelligence**
(https://www.microsoft.com/wdsi/filesubmission) como software legítimo, para evitar falso-positivo
inicial do Defender. Re-submeter a cada versão nova relevante.

## Próximo passo

1. Decidir o nome jurídico do "Editor" (= razão social no certificado).
2. Escolher OV + (token ou cloud signing).
3. Comprar e iniciar a validação da empresa (é o que demora).
4. Quando o cert/HSM estiver pronto, me avisar para eu ligar o gancho no `build-agent-msi.ps1`
   e no job de CI.
