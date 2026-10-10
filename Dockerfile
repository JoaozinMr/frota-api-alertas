# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore em camada separada para aproveitar cache.
COPY src/FrotaAlertas/FrotaAlertas.csproj src/FrotaAlertas/
RUN dotnet restore src/FrotaAlertas/FrotaAlertas.csproj

COPY src/ src/
RUN dotnet publish src/FrotaAlertas/FrotaAlertas.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 8080

COPY --from=build /app/publish .

USER app

# A imagem aspnet não tem curl: faz um GET /health via bash + /dev/tcp e exige status 200.
HEALTHCHECK --interval=10s --timeout=3s --start-period=15s --retries=3 \
  CMD bash -c 'exec 3<>/dev/tcp/127.0.0.1/8080 && printf "GET /health HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n" >&3 && head -n1 <&3 | grep -q " 200 "' || exit 1

ENTRYPOINT ["dotnet", "FrotaAlertas.dll"]
