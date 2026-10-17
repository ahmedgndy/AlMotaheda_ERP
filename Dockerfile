# The API as a Linux container image.
#
#   docker build -t almotaheda-erp-api .
#
# Settings come from environment variables at run time (never baked into the image):
#   ConnectionStrings__Erp, Access__Tokens__SigningKey, Archive__Storage__RootPath, ...
# See deploy/README.md.

# ---- Project files only -----------------------------------------------------------------------------------------
# Every .csproj with its folder, nothing else, so the restore layer below stays cached until a project file changes.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS projects
WORKDIR /src
COPY src/ src/
RUN find src -type f ! -name '*.csproj' -delete

# ---- Build ------------------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY --from=projects /src/src/ src/
RUN dotnet restore src/Host/AlMotaheda.Api/AlMotaheda.Api.csproj

COPY src/ src/
RUN dotnet publish src/Host/AlMotaheda.Api/AlMotaheda.Api.csproj \
        --configuration Release --no-restore --output /app /p:UseAppHost=false

# ---- Run --------------------------------------------------------------------------------------------------------
# The floating 10.0 tag always has the newest .NET 10 patch (security fixes such as System.Security.Cryptography.Pkcs
# come with the runtime), so rebuild the image regularly.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# The company time zone (Projects__TimeZone, default Africa/Cairo) needs time zone data.
RUN apt-get update \
    && apt-get install -y --no-install-recommends tzdata \
    && rm -rf /var/lib/apt/lists/*

# Uploaded project documents. Mount a backed-up volume here.
RUN mkdir -p /data/archive && chown "$APP_UID" /data/archive
VOLUME /data/archive

COPY --from=build /app .

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    Archive__Storage__RootPath=/data/archive

# Run as the image's built-in non-root user.
USER $APP_UID
EXPOSE 8080

# /health/live only says the process answers; it does not depend on the database.
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET /health/live HTTP/1.0\\r\\nHost: localhost\\r\\n\\r\\n' >&3 && head -n 1 <&3 | grep -q ' 200 '"]

ENTRYPOINT ["dotnet", "AlMotaheda.Api.dll"]
