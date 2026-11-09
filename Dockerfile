# syntax=docker/dockerfile:1
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
ARG VERSION=0.1.1
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ src/
RUN case "$TARGETARCH" in \
      amd64) RID=linux-x64 ;; \
      arm64) RID=linux-arm64 ;; \
      *) echo "Unsupported TARGETARCH: $TARGETARCH" >&2; exit 1 ;; \
    esac \
 && dotnet publish src/SemanticBridge/SemanticBridge.csproj -c Release -r "$RID" -p:Version="$VERSION" -o /out

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS final
COPY --from=build /out/sbridge /usr/local/bin/sbridge
# Licence texts, including the Microsoft ADOMD.NET / AMO terms for the bundled client libraries.
COPY LICENSE THIRD-PARTY-NOTICES.md /usr/share/doc/semantic-bridge/
COPY licenses/ /usr/share/doc/semantic-bridge/licenses/
# The base image ships a non-root "app" user.
USER app
EXPOSE 8080
# No token, secrets or write opt-in are baked in. Binding to 0.0.0.0 requires SBRIDGE_HTTP_TOKEN (or --no-auth);
# without it the container refuses to start. Credentials come from SBRIDGE_* env vars or a mounted config file.
ENTRYPOINT ["sbridge"]
CMD ["mcp", "--http", "--host", "0.0.0.0", "--port", "8080"]
