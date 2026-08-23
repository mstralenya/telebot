FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["telebot.fsproj", "./"]
RUN dotnet restore "telebot.fsproj"
COPY . .
RUN dotnet publish "telebot.fsproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final
# FFmpeg/ffprobe for media processing, yt-dlp + deno for YouTube downloads
RUN apk add --no-cache ffmpeg yt-dlp deno
RUN mkdir -p /app/data && chown -R $APP_UID /app
USER $APP_UID
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Telebot.dll"]
