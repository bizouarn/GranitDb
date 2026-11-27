# Contexte de build : la racine du dépôt.
#   docker build -f api/Dockerfile -t granitdb-api .

FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS base
WORKDIR /app
# Les données (catalogue SQLite, fichiers importés) vivent dans /data : montez-y un volume.
RUN mkdir -p /data && chown $APP_UID /data
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Catalog__DataDirectory=/data
EXPOSE 8080
VOLUME /data

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY api/GranitDB.API.csproj api/
RUN dotnet restore api/GranitDB.API.csproj
COPY api/ api/
COPY shop.sqlite ./
RUN dotnet publish api/GranitDB.API.csproj -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
# Licence du projet et mentions des composants tiers.
COPY LICENSE docs/THIRD-PARTY-NOTICES.md /licenses/
ENTRYPOINT ["dotnet", "GranitDB.API.dll"]
