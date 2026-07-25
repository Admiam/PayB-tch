# syntax=docker/dockerfile:1
# Multi-stage build for Paybitch.Api (.NET 10). Build on the SDK; run on a chiseled, non-root aspnet
# runtime. §6 (containers) + EXT-D8i/l (the image is the artifact restored on the Hetzner host — identical
# bytes, migrations applied as a discrete pre-traffic step, never migrate-on-startup).

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, on just the project graph, so the layer caches until a .csproj or props file changes.
COPY Directory.Build.props ./
COPY Paybitch.sln ./
COPY src/Paybitch.Api/Paybitch.Api.csproj                     src/Paybitch.Api/
COPY src/Paybitch.Infrastructure/Paybitch.Infrastructure.csproj src/Paybitch.Infrastructure/
COPY src/Paybitch.Domain/Paybitch.Domain.csproj               src/Paybitch.Domain/
RUN dotnet restore src/Paybitch.Api/Paybitch.Api.csproj

# Now the sources, and publish a framework-dependent, trimmed-of-symbols release.
COPY src/ src/
RUN dotnet publish src/Paybitch.Api/Paybitch.Api.csproj \
        -c Release \
        -o /app/publish \
        --no-restore \
        /p:UseAppHost=false

# ---- runtime ----
# chiseled-extra: no shell/package manager (small attack surface, non-root) BUT ships ICU + tzdata, which the
# app needs because InvariantGlobalization=false (Directory.Build.props). The plain -chiseled image omits ICU.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS runtime
WORKDIR /app

# The chiseled images predefine a non-root user (UID 1654); run as it.
USER $APP_UID

# Non-root aspnet images already default ASPNETCORE_HTTP_PORTS=8080; make the contract explicit.
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

COPY --from=build /app/publish ./
ENTRYPOINT ["dotnet", "Paybitch.Api.dll"]
