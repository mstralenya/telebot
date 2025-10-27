FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine AS base
# Install FFmpeg and yt-dlp using apk
RUN apk add --no-cache ffmpeg yt-dlp
USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["telebot.fsproj", "./"]
RUN dotnet restore "telebot.fsproj"
COPY . .
WORKDIR "/src/"
RUN dotnet build "telebot.fsproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "telebot.fsproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Telebot.dll"]
