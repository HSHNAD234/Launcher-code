FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY HSHONAD.UpdateApi.csproj ./
RUN dotnet restore ./HSHONAD.UpdateApi.csproj
COPY . ./
RUN dotnet publish ./HSHONAD.UpdateApi.csproj -c Release -o /app/publish --no-restore
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish ./
ENV ASPNETCORE_URLS=http://0.0.0.0:10000
EXPOSE 10000
ENTRYPOINT ["dotnet", "HSHONAD.UpdateApi.dll"]
