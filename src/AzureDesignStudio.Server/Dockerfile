#See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BuildConfiguration=Release
WORKDIR /src
RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 \
    && rm -rf /var/lib/apt/lists/* \
    && ln -s /usr/bin/python3 /usr/local/bin/python \
    && dotnet workload install wasm-tools
# The build context is the repository root (see DockerBuild.ps1 and publish.yml), so the
# root build inputs — the SDK pin, central package versions, warning policy and audit
# expiry target — are copied before the restore. With a src/-relative context the restore
# fails NU1015 on every versionless PackageReference.
COPY ["global.json", "/src/"]
COPY ["Directory.Build.props", "/src/"]
COPY ["Directory.Packages.props", "/src/"]
COPY ["Directory.Build.targets", "/src/"]
COPY ["src/AzureDesignStudio.Server/AzureDesignStudio.Server.csproj", "AzureDesignStudio.Server/"]
COPY ["src/AzureDesignStudio.SharedModels/AzureDesignStudio.SharedModels.csproj", "AzureDesignStudio.SharedModels/"]
COPY ["src/AzureDesignStudio/AzureDesignStudio.csproj", "AzureDesignStudio/"]
COPY ["src/AzureDesignStudio.Core/AzureDesignStudio.Core.csproj", "AzureDesignStudio.Core/"]
COPY ["src/AzureDesignStudio.AzureResources/AzureDesignStudio.AzureResources.csproj", "AzureDesignStudio.AzureResources/"]
COPY ["src/AzureDesignStudio.SourceGeneration/AzureDesignStudio.SourceGeneration.csproj", "AzureDesignStudio.SourceGeneration/"]
COPY ["src/AzureDesignStudio.Discovery/AzureDesignStudio.Discovery.csproj", "AzureDesignStudio.Discovery/"]
COPY ["src/AzureDesignStudio.Server/packages.lock.json", "AzureDesignStudio.Server/"]
COPY ["src/AzureDesignStudio.SharedModels/packages.lock.json", "AzureDesignStudio.SharedModels/"]
COPY ["src/AzureDesignStudio/packages.lock.json", "AzureDesignStudio/"]
COPY ["src/AzureDesignStudio.Core/packages.lock.json", "AzureDesignStudio.Core/"]
COPY ["src/AzureDesignStudio.AzureResources/packages.lock.json", "AzureDesignStudio.AzureResources/"]
COPY ["src/AzureDesignStudio.SourceGeneration/packages.lock.json", "AzureDesignStudio.SourceGeneration/"]
COPY ["src/AzureDesignStudio.Discovery/packages.lock.json", "AzureDesignStudio.Discovery/"]
# The Razor SDK only injects the ASP.NET Core assets PackageDownload when the project
# evaluates at least one .razor content item (Microsoft.NET.Sdk.Web.ProjectSystem.targets),
# so the Wasm project's committed locked graph is not reproducible from its csproj alone.
COPY ["src/AzureDesignStudio/*.razor", "/src/AzureDesignStudio/"]
RUN dotnet restore "AzureDesignStudio.Server/AzureDesignStudio.Server.csproj" --locked-mode
COPY src/ ./
WORKDIR "/src/AzureDesignStudio.Server"
RUN dotnet build "AzureDesignStudio.Server.csproj" -c "$BuildConfiguration" --no-restore -o /app/build

FROM build AS publish
RUN dotnet publish "AzureDesignStudio.Server.csproj" -c "$BuildConfiguration" --no-restore -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "AzureDesignStudio.Server.dll"]
