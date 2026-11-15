# syntax=docker/dockerfile:1
#
# The ConfigFerret web app. Build from the repository root:
#
#   docker build -f src/ConfigFerret.Web/Dockerfile -t configferret-web .
#   docker run --rm -p 127.0.0.1:8080:8080 configferret-web
#
# The build stages run on the build machine's own platform and cross-compile, so a multi-platform build
# (--platform linux/amd64,linux/arm64) needs no emulation: the final stage only copies files.

ARG DOTNET_VERSION=10.0

# ---- helm and kubectl, so charts and overlays are rendered (kubectl also does Kustomize) ----
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS tools
ARG TARGETARCH
ARG INSTALL_TOOLS=true
ARG HELM_VERSION=v3.19.0
ARG KUBECTL_VERSION=v1.37.2
RUN set -eu; \
    mkdir -p /tools; \
    if [ "$INSTALL_TOOLS" != "true" ]; then exit 0; fi; \
    cd /tmp; \
    curl -fsSLO "https://get.helm.sh/helm-${HELM_VERSION}-linux-${TARGETARCH}.tar.gz"; \
    curl -fsSLO "https://get.helm.sh/helm-${HELM_VERSION}-linux-${TARGETARCH}.tar.gz.sha256sum"; \
    sha256sum -c "helm-${HELM_VERSION}-linux-${TARGETARCH}.tar.gz.sha256sum"; \
    tar -xzf "helm-${HELM_VERSION}-linux-${TARGETARCH}.tar.gz"; \
    install -m 0755 "linux-${TARGETARCH}/helm" /tools/helm; \
    curl -fsSLo kubectl "https://dl.k8s.io/release/${KUBECTL_VERSION}/bin/linux/${TARGETARCH}/kubectl"; \
    echo "$(curl -fsSL "https://dl.k8s.io/release/${KUBECTL_VERSION}/bin/linux/${TARGETARCH}/kubectl.sha256")  kubectl" | sha256sum -c; \
    install -m 0755 kubectl /tools/kubectl

# ---- the app ----
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG TARGETARCH
ARG VERSION=0.0.0-local
WORKDIR /source
ENV DOTNET_NOLOGO=true DOTNET_CLI_TELEMETRY_OPTOUT=true

# One publish, restore included: the framework's Blazor script is added at publish time, and a separate restore of the
# project files alone leaves it out. The package cache is kept between builds instead.
COPY Directory.Build.props README.md LICENSE ./
COPY assets/ assets/
COPY src/ src/
RUN --mount=type=cache,id=configferret-nuget,target=/root/.nuget/packages \
    arch="$TARGETARCH"; [ "$arch" = amd64 ] && arch=x64; \
    dotnet publish src/ConfigFerret.Web/ConfigFerret.Web.csproj -c Release -f net10.0 -r "linux-$arch" \
      --self-contained false -p:Version="$VERSION" -p:DebugType=none -o /app \
    && test -f /app/wwwroot/_framework/blazor.web.js \
    && mkdir -p /data

# ---- the image: the ASP.NET runtime without a shell or package manager, running as a non-root user ----
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-noble-chiseled-extra
ARG VERSION=0.0.0-local
LABEL org.opencontainers.image.title="ConfigFerret" \
      org.opencontainers.image.description="See where every configuration value comes from, in your browser." \
      org.opencontainers.image.source="https://github.com/Moonwalker1307/ConfigFerret" \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.version="${VERSION}"

COPY --from=tools /tools/ /usr/local/bin/
COPY --from=build --chown=$APP_UID:$APP_UID /data /data
COPY --from=build /app /app

ENV ASPNETCORE_HTTP_PORTS=8080 \
    ConfigFerret__DataFolder=/data \
    ConfigFerret__Folders=/repos \
    DOTNET_NOLOGO=true \
    DOTNET_CLI_TELEMETRY_OPTOUT=true

WORKDIR /app
USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=6s --start-period=20s --retries=3 CMD ["/app/ConfigFerret.Web", "healthcheck"]
ENTRYPOINT ["/app/ConfigFerret.Web"]
