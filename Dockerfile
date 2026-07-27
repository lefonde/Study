FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against the project files alone so this layer stays cached while source churns.
COPY src/StudyApp.Core/StudyApp.Core.csproj src/StudyApp.Core/
COPY src/StudyApp.Web/StudyApp.Web.csproj src/StudyApp.Web/
RUN dotnet restore src/StudyApp.Web/StudyApp.Web.csproj

COPY src/ src/
RUN dotnet publish src/StudyApp.Web/StudyApp.Web.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

# /data is the single stateful directory: SQLite database, uploaded materials, backups.
# It must be a mounted volume in production or everything is lost on redeploy.
ENV ASPNETCORE_URLS=http://+:8080 \
    StudyApp__DataDirectory=/data
EXPOSE 8080
VOLUME /data

ENTRYPOINT ["dotnet", "StudyApp.Web.dll"]
