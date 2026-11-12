FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY src/PortHub.Api/PortHub.Api.csproj src/PortHub.Api/
RUN dotnet restore src/PortHub.Api/PortHub.Api.csproj
COPY src/PortHub.Api/ src/PortHub.Api/
RUN dotnet publish src/PortHub.Api/PortHub.Api.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "PortHub.Api.dll"]
