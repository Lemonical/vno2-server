# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
WORKDIR /src
COPY . .
ARG VNO2_SHARED_FEED
RUN --mount=type=secret,id=vno2_nuget_credentials,env=NuGetPackageSourceCredentials_vno2 \
    dotnet restore src/VNO2.Server/VNO2.Server.csproj --locked-mode --configfile NuGet.Config \
    && dotnet publish src/VNO2.Server/VNO2.Server.csproj --no-restore -c Release -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /out .
RUN mkdir /data && chown "$APP_UID" /data
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_HTTP_PORTS=8080
ENV Community__DatabasePath=/data/community.db
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "VNO2.Server.dll"]
