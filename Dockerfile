# Build Stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy solution and project files for optimal layer caching
COPY ChemFlow.sln ./
COPY src/ChemFlow.Domain/ChemFlow.Domain.csproj src/ChemFlow.Domain/
COPY src/ChemFlow.Infrastructure/ChemFlow.Infrastructure.csproj src/ChemFlow.Infrastructure/
COPY src/ChemFlow.Api/ChemFlow.Api.csproj src/ChemFlow.Api/
COPY tests/ChemFlow.Tests/ChemFlow.Tests.csproj tests/ChemFlow.Tests/

# Restore NuGet dependencies
RUN dotnet restore ChemFlow.sln

# Copy full source tree
COPY src/ src/
COPY tests/ tests/

# Execute test suite to enforce build-time validation
RUN dotnet test tests/ChemFlow.Tests/ChemFlow.Tests.csproj --no-restore -c Release

# Publish API to release directory
RUN dotnet publish src/ChemFlow.Api/ChemFlow.Api.csproj -c Release -o /app/publish --no-restore

# Runtime Stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Install curl for container healthchecks
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

# Create storage directory for persistent SQLite database
RUN mkdir -p /app/data

# Copy compiled artifacts from build stage
COPY --from=build /app/publish .

# Set default environment configuration
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:5080
ENV ConnectionStrings__ChemFlowDb="Data Source=/app/data/chemflow.db"

EXPOSE 5080

ENTRYPOINT ["dotnet", "ChemFlow.Api.dll"]
