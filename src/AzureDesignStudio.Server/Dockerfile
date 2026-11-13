# Build context is the REPOSITORY ROOT, not src/ - the central build files
# (global.json, Directory.Build.props, Directory.Packages.props) live there and
# every project needs them to restore:
#
#   docker build -f src/AzureDesignStudio.Server/Dockerfile .
#
# Ignore rules: .dockerignore at the repository root.

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
# .NET 8+ images default to 8080; the Kubernetes manifests and the ingress
# target port 80, so keep the container on 80. TLS terminates at the ingress.
ENV ASPNETCORE_HTTP_PORTS=80
EXPOSE 80

# The SDK tag tracks global.json (10.0.401, rollForward latestPatch). The
# floating 10.0 tag moves to the next feature band, which global.json rejects.
FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
ARG BuildConfiguration=Release
WORKDIR /repo
RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 \
    && rm -rf /var/lib/apt/lists/* \
    && ln -s /usr/bin/python3 /usr/local/bin/python

# global.json pins the workload set, so it must be present before the install.
COPY ["global.json", "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "./"]
RUN dotnet workload install wasm-tools

# Project files and lock files first, so the restore layer survives source edits.
COPY ["src/AzureDesignStudio.Server/AzureDesignStudio.Server.csproj", "src/AzureDesignStudio.Server/packages.lock.json", "src/AzureDesignStudio.Server/"]
COPY ["src/AzureDesignStudio.SharedModels/AzureDesignStudio.SharedModels.csproj", "src/AzureDesignStudio.SharedModels/packages.lock.json", "src/AzureDesignStudio.SharedModels/"]
COPY ["src/AzureDesignStudio/AzureDesignStudio.csproj", "src/AzureDesignStudio/packages.lock.json", "src/AzureDesignStudio/"]
COPY ["src/AzureDesignStudio.Core/AzureDesignStudio.Core.csproj", "src/AzureDesignStudio.Core/packages.lock.json", "src/AzureDesignStudio.Core/"]
COPY ["src/AzureDesignStudio.AzureResources/AzureDesignStudio.AzureResources.csproj", "src/AzureDesignStudio.AzureResources/packages.lock.json", "src/AzureDesignStudio.AzureResources/"]
COPY ["src/AzureDesignStudio.SourceGeneration/AzureDesignStudio.SourceGeneration.csproj", "src/AzureDesignStudio.SourceGeneration/packages.lock.json", "src/AzureDesignStudio.SourceGeneration/"]
COPY ["src/AzureDesignStudio.Discovery/AzureDesignStudio.Discovery.csproj", "src/AzureDesignStudio.Discovery/packages.lock.json", "src/AzureDesignStudio.Discovery/"]
RUN dotnet restore "src/AzureDesignStudio.Server/AzureDesignStudio.Server.csproj"

COPY . .
WORKDIR "/repo/src/AzureDesignStudio.Server"
RUN dotnet build "AzureDesignStudio.Server.csproj" -c "$BuildConfiguration" -o /app/build

FROM build AS publish
RUN dotnet publish "AzureDesignStudio.Server.csproj" -c "$BuildConfiguration" -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "AzureDesignStudio.Server.dll"]
