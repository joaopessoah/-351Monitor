import { Navigate, Route, Routes } from "react-router-dom";
import { RequireAuth } from "@/components/layout/RequireAuth";
import { AppShell } from "@/components/layout/AppShell";
import { LoginPage } from "@/pages/LoginPage";
import { ConvitePage } from "@/pages/ConvitePage";
import { RecuperarSenhaPage } from "@/pages/RecuperarSenhaPage";
import { RedefinirSenhaPage } from "@/pages/RedefinirSenhaPage";
import { TransparenciaPage } from "@/pages/TransparenciaPage";
import { VisaoGeralPage } from "@/pages/VisaoGeralPage";
import { LinhaDoTempoPage } from "@/pages/LinhaDoTempoPage";
import { AppsPage } from "@/pages/AppsPage";
import { DispositivosPage } from "@/pages/DispositivosPage";
import { PessoaPage } from "@/pages/PessoaPage";
import { RelatoriosHubPage } from "@/pages/relatorios/RelatoriosHubPage";
import { JornadaPage } from "@/pages/relatorios/JornadaPage";
import { UsoPage } from "@/pages/relatorios/UsoPage";
import { ExportacoesPage } from "@/pages/relatorios/ExportacoesPage";
import { ConfiguracoesLayout } from "@/pages/configuracoes/ConfiguracoesLayout";
import { UsuariosPage } from "@/pages/configuracoes/UsuariosPage";
import { ChavesPage } from "@/pages/configuracoes/ChavesPage";
import { CategoriasPage } from "@/pages/configuracoes/CategoriasPage";
import { PrivacidadePage } from "@/pages/configuracoes/PrivacidadePage";
import { ColetaPage } from "@/pages/configuracoes/ColetaPage";
import { OrganizacaoPage } from "@/pages/configuracoes/OrganizacaoPage";
import { AuditoriaPage } from "@/pages/configuracoes/AuditoriaPage";
import { ConformidadePage } from "@/pages/configuracoes/ConformidadePage";
import { NotFoundPage } from "@/pages/NotFoundPage";

export function App() {
  return (
    <Routes>
      {/* Rotas públicas */}
      <Route path="/login" element={<LoginPage />} />
      <Route path="/convite/:token" element={<ConvitePage />} />
      <Route path="/recuperar-senha" element={<RecuperarSenhaPage />} />
      <Route path="/redefinir-senha/:token" element={<RedefinirSenhaPage />} />
      <Route path="/transparencia/:slug" element={<TransparenciaPage />} />
      {/* Mesma página, alcançada pelo token do dispositivo: é o link que o tray
          do agente abre na máquina do funcionário, e a resposta soma o bloco
          "Este dispositivo". */}
      <Route path="/t/:token" element={<TransparenciaPage />} />

      {/* Rotas protegidas */}
      <Route element={<RequireAuth />}>
        <Route element={<AppShell />}>
          <Route path="/" element={<Navigate to="/visao-geral" replace />} />
          <Route path="/visao-geral" element={<VisaoGeralPage />} />
          <Route path="/linha-do-tempo" element={<LinhaDoTempoPage />} />
          <Route path="/apps" element={<AppsPage />} />
          <Route path="/relatorios">
            <Route index element={<RelatoriosHubPage />} />
            <Route path="jornada" element={<JornadaPage />} />
            <Route path="uso" element={<UsoPage />} />
            <Route path="exportacoes" element={<ExportacoesPage />} />
          </Route>
          <Route path="/dispositivos" element={<DispositivosPage />} />
          {/* Visão individual do titular (device_user). Sem rota de índice: as
              pessoas são alcançadas pelos relatórios e pela busca do DSR - o
              portal não publica uma lista de pessoas navegável por si só. */}
          <Route path="/pessoas/:id" element={<PessoaPage />} />
          <Route path="/configuracoes" element={<ConfiguracoesLayout />}>
            <Route index element={<Navigate to="/configuracoes/usuarios" replace />} />
            <Route path="usuarios" element={<UsuariosPage />} />
            <Route path="chaves" element={<ChavesPage />} />
            <Route path="categorias" element={<CategoriasPage />} />
            <Route path="privacidade" element={<PrivacidadePage />} />
            <Route path="coleta" element={<ColetaPage />} />
            <Route path="organizacao" element={<OrganizacaoPage />} />
            <Route path="auditoria" element={<AuditoriaPage />} />
            <Route path="conformidade" element={<ConformidadePage />} />
          </Route>
        </Route>
      </Route>

      <Route path="*" element={<NotFoundPage />} />
    </Routes>
  );
}
