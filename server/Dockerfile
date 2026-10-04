# YHDE server image. Built by deploy/compose.yaml (context: server/).
#   docker build -t yhde-server server

# The admin and join pages (React, built with Vite).
FROM node:24-alpine AS ui
WORKDIR /ui
COPY admin-ui/package.json admin-ui/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY admin-ui/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/YHDE.Server/YHDE.Server.csproj src/YHDE.Server/
RUN dotnet restore src/YHDE.Server/YHDE.Server.csproj
COPY src/YHDE.Server/ src/YHDE.Server/
RUN dotnet publish src/YHDE.Server/YHDE.Server.csproj -c Release -o /app --no-restore
# Asset bytes and the editor add-on live on volumes mounted here; they
# inherit this owner.
RUN mkdir -p /data/blobs /data/addon

# Chiseled: no shell or package manager, runs as a non-root user.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled
WORKDIR /app
COPY --from=build /app .
COPY --from=ui /ui/dist ./ui
COPY --from=build --chown=1654:1654 /data /data
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    Yhde__BlobPath=/data/blobs \
    Yhde__AddonPath=/data/addon
EXPOSE 8080
ENTRYPOINT ["dotnet", "YHDE.Server.dll"]
