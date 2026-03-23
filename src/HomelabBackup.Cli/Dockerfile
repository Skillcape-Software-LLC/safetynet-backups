FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /app
COPY . .
RUN dotnet publish src/HomelabBackup.Cli/HomelabBackup.Cli.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    -o /publish

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0
RUN groupadd -r --force backup && id -u backup &>/dev/null || useradd -r -g backup -s /sbin/nologin backup
WORKDIR /app
COPY --from=build /publish .
RUN mkdir -p /logs /data && chown -R backup:backup /app /logs /data
VOLUME ["/keys", "/data", "/config", "/logs"]
USER backup
ENTRYPOINT ["./HomelabBackup.Cli"]
CMD ["backup"]
