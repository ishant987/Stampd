# syntax=docker/dockerfile:1
# Multi-stage Dockerfile for Stampd.UI.
# Build context: repo root

# ----- Build stage -----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json Directory.Build.props Stampd.slnx ./
COPY src/ src/

RUN dotnet publish src/Stampd.UI/Stampd.UI.csproj \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false

# ----- Runtime stage -----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:5170 \
    ASPNETCORE_ENVIRONMENT=Development

EXPOSE 5170

ENTRYPOINT ["dotnet", "Stampd.UI.dll"]
