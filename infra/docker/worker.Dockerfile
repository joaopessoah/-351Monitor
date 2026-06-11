# syntax=docker/dockerfile:1
# =============================================================================
# +351 Monitor — imagem do Worker (pipeline de intervalização + jobs Quartz.NET)
# Contexto de build: RAIZ do repositório.
#   docker build -f infra/docker/worker.Dockerfile -t m351/worker .
# =============================================================================

# ---------- Estágio 1: publish do Worker (.NET 8) ------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS worker-build
WORKDIR /src
# Copia a árvore backend inteira (bin/obj/tests excluídos via
# worker.Dockerfile.dockerignore) — robusto a Directory.Build.props/nuget.config.
COPY backend/ backend/
RUN dotnet publish backend/src/M351.Worker/M351.Worker.csproj -c Release -o /app/publish /p:UseAppHost=false

# ---------- Estágio 2: runtime final -------------------------------------------
# aspnet:8.0 (superconjunto de runtime:8.0) para suportar /healthz hospedado
# via Microsoft.AspNetCore.App caso o Worker exponha endpoint de saúde.
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=worker-build /app/publish .
ENV DOTNET_EnableDiagnostics=0
# Diretório dos CSVs exportados (F3.5) ANTES do USER app: o volume nomeado herda o dono
# do caminho da imagem na primeira montagem — sem isto ficaria root e o worker não gravaria
RUN mkdir -p /var/lib/m351/exports && chown -R app:app /var/lib/m351
USER app
ENTRYPOINT ["dotnet", "M351.Worker.dll"]
