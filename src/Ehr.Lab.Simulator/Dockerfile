# The simulated lab (the expansion plan's step 12, src/Ehr.Lab.Simulator): the local stack's lab service
# (infra/compose/compose.yaml), built there and by CI's stack job, never published. Build context is the repository
# root. Like src/Ehr.Web/Dockerfile, the build stage runs on the builder's own architecture and compiles for the target
# one, so an x86-64 machine builds the arm64 image (and the other way round) without emulating the .NET SDK.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

# Restore first, from the project file alone, so code changes don't invalidate the package cache.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Ehr.Lab.Simulator/Ehr.Lab.Simulator.csproj src/Ehr.Lab.Simulator/
RUN dotnet restore src/Ehr.Lab.Simulator/Ehr.Lab.Simulator.csproj --arch $TARGETARCH

COPY src/Ehr.Lab.Simulator/ src/Ehr.Lab.Simulator/
# The contract it shares with the on-prem app, compiled in (Ehr.Lab.Simulator.csproj).
COPY src/Ehr.Web/Api/Lab/LabApiContract.cs src/Ehr.Web/Api/Lab/
RUN dotnet publish src/Ehr.Lab.Simulator/Ehr.Lab.Simulator.csproj --configuration Release --no-restore --arch $TARGETARCH --output /app

# The .NET runtime alone: it serves nothing, and talks HTTP as a client.
FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
# The image ships a non-root user but starts as root unless told otherwise.
USER $APP_UID
ENTRYPOINT ["dotnet", "Ehr.Lab.Simulator.dll"]
