// =============================================================================
// Sites (/sites): curadoria dos DOMÍNIOS que a navegação da equipe acessou.
// - Fonte: GET /site-catalog (recorte do tenant, janela fixa de 30 dias, máx.
//   500 itens) - mesmo shape e mesma cobertura da tela de Aplicativos.
// - Classificação inline por linha (admin/owner) e aplicação em LOTE das
//   sugestões do dicionário brasileiro de sites, sempre com prévia.
// - A regra de SITE vence a de APP nos baldes de classificação: é por isso que
//   classificar "mercadolivre.com.br" muda o número mesmo com o navegador
//   classificado como Navegação. A tela diz isso em voz alta, porque é a
//   pergunta nº 1 de quem vê as duas telas pela primeira vez.
// - O produto coleta o DOMÍNIO, nunca o endereço completo, e nunca em janela
//   anônima - o aviso fixo no topo repete isso para quem opera a tela.
// Viewer é somente leitura.
// =============================================================================

import { useMemo, useState } from "react";
import { Link } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { Globe, Info, Lightbulb, ShieldCheck } from "lucide-react";
import { api } from "@/lib/api";
import { classificationColor, classificationLabel, UNCATEGORIZED_LABEL } from "@/lib/classification";
import { formatDuration } from "@/lib/format";
import { genericErrorMessage } from "@/lib/messages";
import { isAdmin } from "@/lib/roles";
import type { CategoriesResponse, MeResponse, SiteCatalogItem, SiteCatalogResponse } from "@/lib/types";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Card, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { CoverageBadge } from "@/components/apps/CoverageBadge";
import { classificationCoverage } from "@/components/apps/classificationCoverage";
import {
  SiteCategoryInlineSelect,
  useApplySiteCategoryBatch,
} from "@/components/sites/SiteCategoryControls";
import type { SiteBatchApplyItem } from "@/components/sites/SiteCategoryControls";

export function SitesPage() {
  const [busca, setBusca] = useState("");
  const [soSemCategoria, setSoSemCategoria] = useState(false);
  const [erro, setErro] = useState<string | null>(null);
  const [aplicadas, setAplicadas] = useState<number | null>(null);
  const [previa, setPrevia] = useState(false);

  const meQuery = useQuery({ queryKey: ["me"], queryFn: () => api<MeResponse>("/me") });
  const podeEditar = isAdmin(meQuery.data);

  const catalogoQuery = useQuery({
    queryKey: ["site-catalog", { q: busca, uncategorized: soSemCategoria }],
    queryFn: () => {
      const params = new URLSearchParams({ sort: "impacto" });
      if (busca.trim().length > 0) params.set("q", busca.trim());
      if (soSemCategoria) params.set("uncategorized", "true");
      return api<SiteCatalogResponse>(`/site-catalog?${params.toString()}`);
    },
    placeholderData: (previous) => previous,
  });

  const categoriasQuery = useQuery({
    queryKey: ["categories"],
    queryFn: () => api<CategoriesResponse>("/categories"),
  });

  const lote = useApplySiteCategoryBatch();

  const categorias = useMemo(() => categoriasQuery.data?.items ?? [], [categoriasQuery.data]);
  const itens = catalogoQuery.data?.items ?? [];

  /**
   * Sugestões aplicáveis: site AINDA sem categoria cuja categoria sugerida pelo
   * dicionário EXISTE nesta organização. O lote é declarativo no backend
   * (sobrescreveria mapeamento existente), então é esta tela que garante nunca
   * enviar um site que a organização já classificou - a decisão dela vence.
   */
  const sugestoes = useMemo(() => {
    const porNome = new Map(categorias.map((c) => [c.name, c.id]));
    const resultado: { site: SiteCatalogItem; categoriaId: string; categoriaNome: string }[] = [];
    for (const site of itens) {
      if (site.category !== null || site.default_category === null) continue;
      const categoriaId = porNome.get(site.default_category);
      if (categoriaId === undefined) continue;
      resultado.push({ site, categoriaId, categoriaNome: site.default_category });
    }
    return resultado;
  }, [itens, categorias]);

  const cobertura = classificationCoverage(
    catalogoQuery.data?.total_seconds_active ?? 0,
    catalogoQuery.data?.uncategorized_seconds_active ?? 0,
  );

  async function aplicarLote() {
    const items: SiteBatchApplyItem[] = sugestoes.map((s) => ({
      siteId: s.site.site_id,
      categoryId: s.categoriaId,
    }));
    try {
      const total = await lote.apply(items);
      setAplicadas(total);
      setPrevia(false);
    } catch {
      // useApplySiteCategoryBatch já guardou a mensagem em lote.error
    }
  }

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h1 className="flex items-center gap-2 text-2xl font-semibold tracking-tight">
            <Globe className="h-6 w-6 text-muted-foreground" aria-hidden />
            Sites
          </h1>
          <p className="text-sm text-muted-foreground">
            Domínios acessados pela equipe nos últimos 30 dias, para classificar como qualquer
            aplicativo.
          </p>
        </div>
        <CoverageBadge coverage={cobertura} />
      </div>

      <Card className="border-l-4 border-l-primary/60">
        <CardHeader className="gap-2 py-4">
          <CardTitle className="flex items-center gap-2 text-base">
            <ShieldCheck className="h-4 w-4 text-muted-foreground" aria-hidden />O que entra nesta lista
          </CardTitle>
          <CardDescription>
            Somente o <strong>domínio</strong> do site aberto no navegador — nunca o endereço
            completo, nunca o conteúdo da página e nunca janelas anônimas ou privativas. A coleta
            pode ser desligada a qualquer momento em{" "}
            <Link to="/configuracoes/coleta" className="underline underline-offset-2">
              Configurações › Coleta
            </Link>
            .
          </CardDescription>
        </CardHeader>
      </Card>

      <Card>
        <CardHeader className="gap-2 py-4">
          <CardTitle className="flex items-center gap-2 text-base">
            <Info className="h-4 w-4 text-muted-foreground" aria-hidden />
            Como a classificação de site conversa com a de aplicativo
          </CardTitle>
          <CardDescription>
            Quando um trecho de navegação tem site conhecido, é a categoria do <strong>site</strong>{" "}
            que vale — ela vence a categoria do navegador, que é mais genérica. Nos trechos sem site
            (ou fora do navegador) continua valendo a categoria do{" "}
            <Link to="/apps" className="underline underline-offset-2">
              aplicativo
            </Link>
            .
          </CardDescription>
        </CardHeader>
      </Card>

      <Card>
        <CardHeader className="gap-3 py-4">
          <div className="flex flex-wrap items-center gap-3">
            <Input
              value={busca}
              onChange={(e) => setBusca(e.target.value)}
              placeholder="Buscar domínio"
              className="h-9 w-full max-w-xs"
              aria-label="Buscar site por domínio"
            />
            <label className="flex items-center gap-2 text-sm">
              <input
                type="checkbox"
                checked={soSemCategoria}
                onChange={(e) => setSoSemCategoria(e.target.checked)}
                className="h-4 w-4 rounded border-input"
              />
              Somente sem categoria
            </label>
            <span className="text-sm text-muted-foreground">
              {catalogoQuery.data !== undefined
                ? `${catalogoQuery.data.uncategorized_count} site(s) sem categoria`
                : ""}
            </span>
          </div>

          {podeEditar && sugestoes.length > 0 && (
            <div className="rounded-md border border-dashed bg-accent/30 p-3 text-sm">
              <div className="flex flex-wrap items-center justify-between gap-3">
                <span className="flex items-center gap-2">
                  <Lightbulb className="h-4 w-4 text-muted-foreground" aria-hidden />
                  {sugestoes.length === 1
                    ? "1 site tem sugestão do dicionário brasileiro"
                    : `${sugestoes.length} sites têm sugestão do dicionário brasileiro`}
                </span>
                <Button
                  variant="outline"
                  size="sm"
                  disabled={lote.progress !== null}
                  onClick={() => setPrevia((v) => !v)}
                >
                  {previa ? "Fechar prévia" : "Ver e aplicar sugestões"}
                </Button>
              </div>

              {previa && (
                <div className="mt-3 space-y-2">
                  <ul className="max-h-56 space-y-1 overflow-y-auto text-xs">
                    {sugestoes.map((s) => (
                      <li key={s.site.site_id} className="flex items-center justify-between gap-3">
                        <span className="truncate">{s.site.domain}</span>
                        <span className="shrink-0 text-muted-foreground">→ {s.categoriaNome}</span>
                      </li>
                    ))}
                  </ul>
                  <div className="flex items-center gap-3">
                    <Button size="sm" disabled={lote.progress !== null} onClick={() => void aplicarLote()}>
                      {lote.progress !== null
                        ? `Aplicando ${lote.progress.done}/${lote.progress.total}…`
                        : `Aplicar ${sugestoes.length} sugestão(ões)`}
                    </Button>
                    <span className="text-xs text-muted-foreground">
                      Aplicar reagrega os últimos 30 dias. A decisão pode ser mudada depois, site a site.
                    </span>
                  </div>
                </div>
              )}
            </div>
          )}

          {lote.error !== null && (
            <p className="text-sm text-viz-improdutivo" role="alert">
              {lote.error}
            </p>
          )}
          {aplicadas !== null && lote.error === null && (
            <p className="text-sm text-muted-foreground" role="status">
              {aplicadas === 1 ? "1 site classificado." : `${aplicadas} sites classificados.`}
            </p>
          )}
          {erro !== null && (
            <p className="text-sm text-viz-improdutivo" role="alert">
              {erro}
            </p>
          )}
        </CardHeader>

        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b text-left text-xs font-medium uppercase tracking-wide text-muted-foreground">
                <th scope="col" className="px-6 py-2">
                  Site
                </th>
                <th scope="col" className="px-3 py-2">
                  Categoria
                </th>
                <th scope="col" className="px-3 py-2 text-right">
                  Tempo ativo (30d)
                </th>
                <th scope="col" className="px-3 py-2 text-right">
                  Dispositivos
                </th>
              </tr>
            </thead>
            <tbody>
              {catalogoQuery.isLoading ? (
                Array.from({ length: 8 }, (_, i) => (
                  <tr key={i} className="border-b last:border-b-0">
                    <td colSpan={4} className="px-6 py-2">
                      <Skeleton className="h-8 w-full" />
                    </td>
                  </tr>
                ))
              ) : catalogoQuery.isError ? (
                <tr>
                  <td colSpan={4} className="px-6 py-10 text-center text-sm">
                    <span className="block text-muted-foreground">
                      {genericErrorMessage(catalogoQuery.error)}
                    </span>
                    <Button variant="outline" size="sm" className="mt-3" onClick={() => void catalogoQuery.refetch()}>
                      Tentar novamente
                    </Button>
                  </td>
                </tr>
              ) : itens.length === 0 ? (
                <tr>
                  <td colSpan={4} className="px-6 py-10 text-center text-sm text-muted-foreground">
                    Nenhum site nos últimos 30 dias. Sites aparecem aqui conforme a navegação da
                    equipe for coletada.
                  </td>
                </tr>
              ) : (
                itens.map((site) => (
                  <tr key={site.site_id} className="border-b transition-colors last:border-b-0 hover:bg-accent/50">
                    <td className="px-6 py-2">
                      <span className="block max-w-[22rem] truncate font-medium">
                        {site.custom_display_name ?? site.display_name}
                      </span>
                      {(site.custom_display_name ?? site.display_name) !== site.domain && (
                        <span className="block max-w-[22rem] truncate text-xs text-muted-foreground">
                          {site.domain}
                        </span>
                      )}
                    </td>
                    <td className="px-3 py-2">
                      {podeEditar ? (
                        <SiteCategoryInlineSelect
                          siteId={site.site_id}
                          categoryId={site.category?.id ?? null}
                          categoryName={site.category?.name ?? null}
                          customDisplayName={site.custom_display_name}
                          categories={categorias}
                          disabled={lote.progress !== null}
                          onError={(err) => setErro(genericErrorMessage(err))}
                        />
                      ) : (
                        <span className="flex items-center gap-2">
                          <span
                            aria-hidden
                            className="h-2.5 w-2.5 shrink-0 rounded-full"
                            style={{
                              backgroundColor:
                                site.category?.color ?? classificationColor(site.category?.classification ?? null),
                            }}
                          />
                          <span className={cn(site.category === null && "text-muted-foreground")}>
                            {site.category?.name ?? UNCATEGORIZED_LABEL}
                          </span>
                          {site.category !== null && (
                            <span className="text-xs text-muted-foreground">
                              ({classificationLabel(site.category.classification)})
                            </span>
                          )}
                        </span>
                      )}
                    </td>
                    <td className="whitespace-nowrap px-3 py-2 text-right tabular-nums">
                      {formatDuration(site.seconds_active_30d)}
                    </td>
                    <td className="whitespace-nowrap px-3 py-2 text-right tabular-nums">
                      {site.device_count_30d}
                    </td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>
      </Card>
    </div>
  );
}
