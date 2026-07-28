# Images are pinned rather than floating on :10.0 so a container build reproduces something
# actually verified instead of drifting when the tag moves to a new SDK feature band.
FROM mcr.microsoft.com/dotnet/sdk:10.0.203 AS build
WORKDIR /src

# Warm the NuGet cache from the project files alone, so this layer stays cached while source
# churns and the restore during publish is then nearly instant.
COPY src/StudyApp.Core/StudyApp.Core.csproj src/StudyApp.Core/
COPY src/StudyApp.Web/StudyApp.Web.csproj src/StudyApp.Web/
RUN dotnet restore src/StudyApp.Web/StudyApp.Web.csproj

COPY src/ src/

# Deliberately NOT --no-restore. That restore above ran with only the .csproj files present —
# no source, no wwwroot — which leaves static web asset resolution incomplete. Reusing it made
# publish silently omit wwwroot/_framework, so the deployed app served every asset except
# Blazor's runtime: the site rendered and styled correctly but no button did anything.
# Letting publish restore again costs a couple of seconds (packages are already cached) and is
# the difference between a working app and a dead one.
RUN dotnet publish src/StudyApp.Web/StudyApp.Web.csproj -c Release -o /app

# Assert here as well as in the runtime stage, so a failure says whether publish never emitted
# the Blazor runtime or the copy lost it. Prints the SDK actually used, because a floating base
# image silently changing feature band is what broke this the first time.
RUN dotnet --version \
 && ls -la /app/wwwroot/ \
 && test -f /app/wwwroot/_framework/blazor.web.js \
    || (echo "FATAL: publish did not emit wwwroot/_framework/blazor.web.js" && exit 1)

FROM mcr.microsoft.com/dotnet/aspnet:10.0.10
WORKDIR /app
COPY --from=build /app .

# Fail the build rather than ship an app whose interactive runtime is missing. Without
# blazor.web.js the site renders but every button is dead, which is easy to mistake for an
# application bug — exactly what happened once. Catch it here instead of in the browser.
RUN test -f /app/wwwroot/_framework/blazor.web.js \
    || (echo "FATAL: wwwroot/_framework/blazor.web.js missing from publish output." && exit 1)

# /data is the single stateful directory: SQLite database, uploaded materials, backups.
# It must be a mounted volume in production or everything is lost on redeploy.
ENV ASPNETCORE_URLS=http://+:8080 \
    StudyApp__DataDirectory=/data
EXPOSE 8080
VOLUME /data

ENTRYPOINT ["dotnet", "StudyApp.Web.dll"]
