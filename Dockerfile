# Both images are pinned, not floating on :10.0.
#
# The floating tag moved to SDK 10.0.302, a different feature band from the 10.0.2xx used
# locally, and that build stopped emitting wwwroot/_framework into the publish output — so the
# deployed app 404'd on blazor.web.js and every interactive control was dead while the rest of
# the site looked fine. Pinning makes the container reproduce a build that is actually verified
# rather than silently changing under us. Bump deliberately, and re-test interactivity after.
FROM mcr.microsoft.com/dotnet/sdk:10.0.203 AS build
WORKDIR /src

# Restore against the project files alone so this layer stays cached while source churns.
COPY src/StudyApp.Core/StudyApp.Core.csproj src/StudyApp.Core/
COPY src/StudyApp.Web/StudyApp.Web.csproj src/StudyApp.Web/
RUN dotnet restore src/StudyApp.Web/StudyApp.Web.csproj

COPY src/ src/
RUN dotnet publish src/StudyApp.Web/StudyApp.Web.csproj -c Release -o /app --no-restore

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
