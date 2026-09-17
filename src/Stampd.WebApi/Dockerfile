# syntax=docker/dockerfile:1
# Multi-stage Dockerfile for Stampd.WebApi.
#
# Build context: the repo root (so the build can see all project references).
# Build command: docker build -f src/Stampd.WebApi/Dockerfile -t stampd/webapi .

# ----- Build stage -----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution + props first so layer cache stays warm when only source changes.
COPY global.json Directory.Build.props Stampd.slnx ./

COPY src/ src/

RUN dotnet restore src/Stampd.WebApi/Stampd.WebApi.csproj

RUN dotnet publish src/Stampd.WebApi/Stampd.WebApi.csproj \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false \
    /p:CopyOutputSymbolsToPublishDirectory=false

# ----- Runtime stage -----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Install fonts and native libraries so PDFsharp and SkiaSharp run cleanly on Linux.
RUN apt-get update \
 && apt-get install -y --no-install-recommends fonts-dejavu fonts-liberation curl libfontconfig1 \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Persistent state directories — mount volumes here.
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    Stampd__Database__Provider=Sqlite \
    Stampd__Database__SqlitePath=/data/stampd.db \
    Stampd__Storage__FileSystemRoot=/data/documents \
    Stampd__Logging__Directory=/data/logs

RUN mkdir -p /data /data/documents /data/logs

EXPOSE 8080

ENTRYPOINT ["dotnet", "Stampd.WebApi.dll"]
