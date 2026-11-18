# Contexte de build : la racine du dépôt.
#   docker build -f client/Dockerfile -t granitdb-client .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY client/GranitDb.Client.csproj client/
RUN dotnet restore client/GranitDb.Client.csproj
COPY client/ client/
RUN dotnet publish client/GranitDb.Client.csproj -c Release -o /app/publish

FROM nginx:alpine
COPY client/nginx.conf /etc/nginx/conf.d/default.conf
# Écrit wwwroot/appsettings.json au démarrage à partir de la variable API_BASE_URL.
COPY client/docker-entrypoint.sh /docker-entrypoint.d/40-appsettings.sh
RUN chmod +x /docker-entrypoint.d/40-appsettings.sh
COPY --from=build /app/publish/wwwroot /usr/share/nginx/html
ENV API_BASE_URL=http://localhost:5105/
EXPOSE 80
