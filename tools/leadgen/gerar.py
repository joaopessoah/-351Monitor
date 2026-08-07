"""Gerador mensal de leads a partir dos dados abertos do CNPJ (RFB).

Uso típico (mensal):
    python gerar.py                          # mês mais recente, top 300
    python gerar.py --simular --limite 50    # ensaio sem gravar histórico
    python gerar.py --por-vertical 75        # quota por vertical

Saída: saida/leads-AAAA-MM.csv no formato do import do CRM
(empresa;contato;email;whatsapp;estacoes;origem;observacoes;cnpj).
"""

import argparse
import shutil
import sys
import time

import config
import crm
import historico
import pontuar
import rfb
import transformar


def main() -> int:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    p = argparse.ArgumentParser(description="Gera a lista mensal de prospecção (RFB).")
    p.add_argument("--mes", default="auto", help="pasta do espelho (AAAA-MM-DD) ou 'auto'")
    p.add_argument("--limite", type=int, default=300, help="tamanho da lista (padrão 300)")
    p.add_argument("--uf-boost", default=config.UF_BOOST_PADRAO, help="UF priorizada no score")
    p.add_argument("--por-vertical", type=int, default=None,
                   help="quota máxima por vertical (ex.: 75)")
    p.add_argument("--sem-crm", action="store_true", help="pula o dedupe via API do CRM")
    p.add_argument("--refazer", action="store_true", help="reprocessa ignorando o parquet do mês")
    p.add_argument("--simular", action="store_true",
                   help="não grava histórico (saída com sufixo -simulacao)")
    p.add_argument("--manter-zips", action="store_true", help="não apaga os zips ao final")
    p.add_argument("--manter-csv", action="store_true", help="não apaga os CSVs extraídos")
    p.add_argument("--enviar-crm", action="store_true",
                   help="além do CSV, cria os leads direto via API do CRM")
    p.add_argument("--enviar-pool", action="store_true",
                   help="modo fila: envia as melhores empresas para a fila de prospecção do CRM "
                        "(sem CSV e sem histórico — a reconciliação acontece na puxada)")
    p.add_argument("--pool-tamanho", type=int, default=config.POOL_TAMANHO,
                   help=f"tamanho da fila no modo --enviar-pool (padrão {config.POOL_TAMANHO})")
    p.add_argument("--economizar-disco", action="store_true",
                   help="baixa/extrai/apaga arquivo a arquivo (pico ~11 GB em vez de ~30 GB; "
                        "usado no GitHub Actions)")
    args = p.parse_args()

    inicio = time.time()
    print("== Gerador de leads +351 Monitor ==")

    mes = args.mes
    if mes == "auto":
        print("[1] Descobrindo o mês mais recente no espelho...")
        mes = rfb.descobrir_mes_mais_recente()
    print(f"    Mês de referência: {mes}")

    parquet = config.DIR_DATA / mes / "base_filtrada.parquet"
    if not parquet.exists() or args.refazer:
        if args.economizar_disco:
            print("[2-3] Modo econômico: download/extração arquivo a arquivo, apagando após uso.")
        else:
            print("[2] Download dos arquivos da RFB (retomável; pula os completos)...")
            rfb.baixar_mes(mes)
            print("[3] Extração dos CSVs...")
            rfb.extrair_mes(mes)
        print("[4] Transformação (DuckDB)...")
        transformar.transformar(mes, refazer=args.refazer, economizar=args.economizar_disco)
    else:
        print("[2-4] Base do mês já processada (cache); use --refazer para reprocessar.")

    if args.enviar_pool:
        print(f"[5] Modo fila: pontuando e enviando top {args.pool_tamanho} para o CRM...")
        itens = pontuar.gerar_pool_itens(str(parquet), mes, args.pool_tamanho, args.uf_boost.upper())
        gravados = ignorados = 0
        for i in range(0, len(itens), config.POOL_LOTE):
            lote = itens[i:i + config.POOL_LOTE]
            resp = crm.pool_upsert_lote(lote)
            gravados += resp.get("gravados", 0)
            ignorados += resp.get("ignorados", 0)
            print(f"    lote {i // config.POOL_LOTE + 1}/{-(-len(itens) // config.POOL_LOTE)}: "
                  f"+{resp.get('gravados', 0)} gravados")
        stats = crm.pool_stats()
        print("\n== Fila atualizada ==")
        print(f"Enviados: {gravados} gravados, {ignorados} ignorados")
        print(f"Disponíveis na fila: {stats.get('disponiveis')} | já usados: {stats.get('promovidos')}"
              f" | base RFB: {stats.get('mes')}")
        if not args.manter_csv:
            shutil.rmtree(dir_mes_pool := (config.DIR_DATA / mes / "csv"), ignore_errors=True)
        shutil.rmtree(config.DIR_DATA / mes / "tmp", ignore_errors=True)
        if not args.manter_zips:
            shutil.rmtree(config.DIR_DATA / mes / "zips", ignore_errors=True)
        print(f"Tempo total: {time.time() - inicio:.0f}s")
        return 0

    print("[5] Dedupe (histórico local + CRM)...")
    excluir_cnpjs = historico.carregar()
    excluir_emails: set[str] = set()
    print(f"    Histórico local: {len(excluir_cnpjs)} CNPJs já exportados")
    if args.sem_crm:
        print("    CRM: pulado (--sem-crm)")
    else:
        crm_cnpjs, crm_emails = crm.carregar_existentes()
        excluir_cnpjs |= crm_cnpjs
        excluir_emails |= crm_emails
        print(f"    CRM: {len(crm_cnpjs)} CNPJs / {len(crm_emails)} e-mails já cadastrados")

    print("[6] Score e seleção...")
    arq_csv, selecionados, resumo = pontuar.gerar_saida(
        str(parquet), mes, args.limite, args.uf_boost.upper(),
        args.por_vertical, excluir_cnpjs, excluir_emails, args.simular,
    )

    if args.enviar_crm and not args.simular:
        print("[7] Criando leads via API do CRM...")
        criados = duplicados = erros = 0
        with open(arq_csv, encoding="utf-8-sig") as f:
            import csv as _csv
            for linha in _csv.DictReader(f, delimiter=";"):
                try:
                    resp = crm.criar_lead(linha)
                    if resp.get("duplicate_of_lead_id"):
                        duplicados += 1
                    else:
                        criados += 1
                except Exception as e:  # segue o lote; erro vai para o resumo
                    erros += 1
                    print(f"    [erro] {linha['empresa']}: {e}")
        print(f"    Criados: {criados} | flagados duplicados: {duplicados} | erros: {erros}")

    if not args.simular:
        historico.registrar([r["cnpj14"] for r in selecionados], mes)
        print(f"[8] Histórico atualizado (+{len(selecionados)}). Commite data/historico/exportados.csv.")

    # Limpeza: mantém só zips (opcional) e o parquet do mês
    dir_mes = config.DIR_DATA / mes
    if not args.manter_csv:
        shutil.rmtree(dir_mes / "csv", ignore_errors=True)
    shutil.rmtree(dir_mes / "tmp", ignore_errors=True)
    if not args.manter_zips:
        shutil.rmtree(dir_mes / "zips", ignore_errors=True)

    print("\n== Resumo ==")
    print(f"Base filtrada do mês:        {resumo['base']:>7}")
    print(f"Removidos por dedupe:        {resumo['dedupe_historico_crm']:>7}")
    print(f"Candidatos pontuados:        {resumo['candidatos']:>7}")
    print(f"Exportados (top {args.limite}):", " " * max(0, 6 - len(str(args.limite))), resumo["exportados"])
    print("Por vertical:", ", ".join(f"{v}={n}" for v, n in resumo["por_vertical"].items()))
    print("Top UFs:", ", ".join(f"{uf}={n}" for uf, n in resumo["por_uf"].items()))
    print(f"\nArquivo para importar no CRM: {arq_csv}")
    print(f"Análise completa:             {str(arq_csv).replace('.csv', '-completo.csv')}")
    print(f"Tempo total: {time.time() - inicio:.0f}s")
    return 0


if __name__ == "__main__":
    sys.exit(main())
