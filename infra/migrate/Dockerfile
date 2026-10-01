# Brings the primary's database up to date, then exits (see migrate.sh).
# Build context is the repository root (see infra/compose/compose.yaml). Like src/Ehr.Web/Dockerfile, the build
# stage runs on the builder's own architecture and compiles for the target one.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props ./
COPY .config/dotnet-tools.json .config/
RUN dotnet tool restore

# Restore first, from the project files alone, so code changes don't invalidate the package cache.
COPY src/Ehr.Data/Ehr.Data.csproj src/Ehr.Data/
COPY db/migrations/Ehr.Migrations.csproj db/migrations/
RUN dotnet restore db/migrations/Ehr.Migrations.csproj --arch $TARGETARCH

COPY src/Ehr.Data/ src/Ehr.Data/
COPY db/migrations/ db/migrations/
# A self-contained executable that applies any migrations the database doesn't have yet. It needs no .NET.
RUN case "$TARGETARCH" in amd64) rid=linux-x64 ;; arm64) rid=linux-arm64 ;; *) echo "Unsupported architecture: $TARGETARCH" >&2; exit 1 ;; esac \
 && dotnet ef migrations bundle --project db/migrations --configuration Release \
      --self-contained --target-runtime "$rid" --output /out/efbundle

# postgres:18 provides psql, at the server's version, for the hand-written SQL in db/policies.
FROM postgres:18
COPY --from=build /out/efbundle /usr/local/bin/efbundle
COPY db/policies/ /policies/
COPY infra/migrate/migrate.sh /usr/local/bin/migrate.sh
USER postgres
ENTRYPOINT ["/bin/sh", "/usr/local/bin/migrate.sh"]
