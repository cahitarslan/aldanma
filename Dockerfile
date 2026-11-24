# Aldanma API. Build context is the repository root (the engine embeds rules/rules.json).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY rules/rules.json rules/
COPY src/Aldanma.Core/Aldanma.Core.csproj src/Aldanma.Core/
COPY src/Aldanma.Api/Aldanma.Api.csproj src/Aldanma.Api/
RUN dotnet restore src/Aldanma.Api/Aldanma.Api.csproj
COPY src/ src/
RUN dotnet publish src/Aldanma.Api/Aldanma.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_gcServer=0
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Aldanma.Api.dll"]
