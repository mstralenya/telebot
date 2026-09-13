FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
ARG BUILD_CONFIGURATION=Release
# Semver computed by CI (MAJOR.MINOR.PATCH); falls back to 0.0.0-dev for local builds
ARG APP_VERSION=0.0.0-dev
WORKDIR /src
COPY ["telebot.fsproj", "./"]
RUN dotnet restore "telebot.fsproj"
COPY . .
RUN dotnet publish "telebot.fsproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false /p:Version=${APP_VERSION}

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final
# Version of the application, available for debugging/troubleshooting
ARG APP_VERSION=0.0.0-dev
ENV TELEBOT_VERSION=${APP_VERSION}
# FFmpeg/ffprobe for media processing, yt-dlp + deno for YouTube downloads
RUN apk add --no-cache ffmpeg yt-dlp deno
# Mesa VAAPI userspace driver for AMD/Intel hardware video encoding
RUN apk add --no-cache mesa-va-gallium
RUN mkdir -p /app/data && chown -R $APP_UID /app
USER $APP_UID
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Telebot.dll"]
