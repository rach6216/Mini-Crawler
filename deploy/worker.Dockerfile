# Build context: repository root.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY src/Crawler.Core/Crawler.Core.csproj src/Crawler.Core/
COPY src/Crawler.Worker/Crawler.Worker.csproj src/Crawler.Worker/
RUN dotnet restore src/Crawler.Worker/Crawler.Worker.csproj

COPY src/ src/
RUN dotnet publish src/Crawler.Worker/Crawler.Worker.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8081 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8081
HEALTHCHECK --interval=5s --timeout=3s --start-period=20s --retries=20 \
    CMD curl -fsS http://localhost:8081/health/ready || exit 1

USER app
ENTRYPOINT ["dotnet", "Crawler.Worker.dll"]
