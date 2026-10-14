FROM mcr.microsoft.com/dotnet/sdk:11.0.100-preview.6 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Api/Api.csproj -c Release -o /app -p:OpenApiGenerateDocumentsOnBuild=false

FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-preview.6
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "ELifeRPG.Api.dll"]
