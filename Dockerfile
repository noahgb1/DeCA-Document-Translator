# Use the official ASP.NET Core 8.0 runtime as the base image
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080

# Use the .NET SDK 8.0 image to build the application
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Copy project files (each referenced csproj to leverage layer caching)
COPY ["DocumentTranslationService/DocumentTranslationService.csproj", "DocumentTranslationService/"]
COPY ["DocumentTranslation.Web/DocumentTranslation.Web.csproj", "DocumentTranslation.Web/"]


# Restore dependencies
RUN dotnet restore "DocumentTranslation.Web/DocumentTranslation.Web.csproj"

# Copy all source code
COPY . .

# Build the application
WORKDIR "/src/DocumentTranslation.Web"
RUN dotnet build "DocumentTranslation.Web.csproj" -c $BUILD_CONFIGURATION -o /app/build

# Publish the application
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "DocumentTranslation.Web.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# Create the final runtime image
FROM base AS final
WORKDIR /app

# Optional: install curl for health checks
RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/*

# Copy the published output to the runtime container
COPY --from=publish /app/publish .

# Optional: run as non-root user
RUN adduser --disabled-password --gecos '' appuser && chown -R appuser /app
USER appuser

# Environment configuration
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

# Optional: health check endpoint (implement /health in app)
HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1

# Start the app
ENTRYPOINT ["dotnet", "DocumentTranslation.Web.dll"]
