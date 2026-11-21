# syntax=docker/dockerfile:1.7
# ---------------------------------------------------------------- build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
# Optional: trust a corporate TLS-inspection CA during restore:
#   docker build --secret id=extra_ca,src=corp-ca.crt ...
RUN --mount=type=secret,id=extra_ca,required=false \
    if [ -f /run/secrets/extra_ca ]; then cp /run/secrets/extra_ca /usr/local/share/ca-certificates/extra-ca.crt && update-ca-certificates; fi
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props SmartMeter.slnx ./
COPY src/SmartMeter.Domain/SmartMeter.Domain.csproj src/SmartMeter.Domain/
COPY src/SmartMeter.Application/SmartMeter.Application.csproj src/SmartMeter.Application/
COPY src/SmartMeter.Infrastructure/SmartMeter.Infrastructure.csproj src/SmartMeter.Infrastructure/
COPY src/SmartMeter.Api/SmartMeter.Api.csproj src/SmartMeter.Api/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet restore src/SmartMeter.Api/SmartMeter.Api.csproj
COPY src/ src/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet publish src/SmartMeter.Api/SmartMeter.Api.csproj -c Release -o /app --no-restore /p:UseAppHost=false

# ---------------------------------------------------------------- runtime
# Debian-based aspnet image: includes ICU, required by Microsoft.Data.SqlClient.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_gcServer=1 \
    Ingestion__StoragePath=/var/smartmeter/uploads
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /var/smartmeter/uploads && chown -R $APP_UID /var/smartmeter
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "SmartMeter.Api.dll"]
