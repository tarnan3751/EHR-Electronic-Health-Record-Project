# Build context is the repository root (see infra/compose/compose.yaml).
# The build stage runs on the builder's own architecture and compiles for the target one, so an
# x86-64 CI runner builds the arm64 image (and vice versa) without emulating the .NET SDK.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

# Restore first, from the project files alone, so code changes don't invalidate the package cache.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Ehr.Web/Ehr.Web.csproj src/Ehr.Web/
COPY src/Ehr.Domain/Ehr.Domain.csproj src/Ehr.Domain/
COPY src/Ehr.Data/Ehr.Data.csproj src/Ehr.Data/
COPY src/Ehr.Design/Ehr.Design.csproj src/Ehr.Design/
RUN dotnet restore src/Ehr.Web/Ehr.Web.csproj --arch $TARGETARCH

COPY src/Ehr.Web/ src/Ehr.Web/
COPY src/Ehr.Domain/ src/Ehr.Domain/
COPY src/Ehr.Data/ src/Ehr.Data/
COPY src/Ehr.Design/ src/Ehr.Design/
RUN dotnet publish src/Ehr.Web/Ehr.Web.csproj --configuration Release --no-restore --arch $TARGETARCH --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# Where the cloud app keeps its audit events until the on-prem app has them (Ehr:CloudAudit:Directory). Made here, owned
# by the app's user, so a new volume mounted on it starts out writable by that user.
RUN mkdir -p /var/lib/ehr/cloud-audit && chown $APP_UID /var/lib/ehr/cloud-audit
# The image ships a non-root user but starts as root unless told otherwise.
USER $APP_UID
ENTRYPOINT ["dotnet", "Ehr.Web.dll"]
