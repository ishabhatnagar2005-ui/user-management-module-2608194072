FROM mcr.microsoft.com/dotnet/sdk:8.0.428 AS build
WORKDIR /source
COPY UserManagement.csproj ./
RUN dotnet restore UserManagement.csproj
COPY . ./
RUN dotnet publish UserManagement.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false
FROM mcr.microsoft.com/dotnet/aspnet:8.0.22-bookworm-slim
WORKDIR /app
COPY --from=build /app/publish ./
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "UserManagement.dll"]
