# syntax=docker/dockerfile:1
# =============================================================================
# +351 Monitor — imagem da API (ASP.NET Core 8) com a SPA do portal em wwwroot
# Contexto de build: RAIZ do repositório.
#   docker build -f infra/docker/api.Dockerfile -t m351/api .
# =============================================================================

# ---------- Estágio 1: build do portal (Vite + React + TS) -------------------
FROM node:20 AS portal-build
WORKDIR /portal
# Cache de dependências: copia só os manifests antes do código
COPY portal/package.json portal/package-lock.json ./
RUN npm ci
COPY portal/ ./
RUN npm run build
# Saída esperada: /portal/dist

# ---------- Estágio 2: publish da API (.NET 8) --------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS api-build
WORKDIR /src
# Copia a árvore backend inteira (bin/obj/tests excluídos via
# api.Dockerfile.dockerignore) — robusto a Directory.Build.props/nuget.config.
COPY backend/ backend/
RUN dotnet publish backend/src/M351.Api/M351.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

# ---------- Estágio 3: runtime final ------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=api-build /app/publish .
# SPA servida como assets estáticos do ASP.NET Core (Seção 4 do spec)
COPY --from=portal-build /portal/dist ./wwwroot
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
# Diretórios dos CSVs exportados (F3.5) e dos MSIs de auto-update (F4.2) ANTES do
# USER app: o volume nomeado herda o dono do caminho da imagem na primeira montagem —
# sem isto ficaria root e a app não gravaria
RUN mkdir -p /var/lib/m351/exports /var/lib/m351/releases && chown -R app:app /var/lib/m351
# Usuário não-root provido pelas imagens .NET 8
USER app
ENTRYPOINT ["dotnet", "M351.Api.dll"]
