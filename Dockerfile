# Estágio 1: compila e publica só a API.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /fonte

# Primeiro só os arquivos de dependências: enquanto eles não mudam, o restore fica em cache.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ReservaMakerspace.Dominio/ReservaMakerspace.Dominio.csproj src/ReservaMakerspace.Dominio/
COPY src/ReservaMakerspace.Api/ReservaMakerspace.Api.csproj src/ReservaMakerspace.Api/
RUN dotnet restore src/ReservaMakerspace.Api/ReservaMakerspace.Api.csproj

COPY src/ src/
RUN dotnet publish src/ReservaMakerspace.Api/ReservaMakerspace.Api.csproj -c Release --no-restore -o /publicado

# Estágio 2: só o runtime, com usuário sem privilégio.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /publicado .
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "ReservaMakerspace.Api.dll"]
