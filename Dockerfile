FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY nAlbum.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
RUN useradd --create-home --uid 10001 bot && mkdir /data && chown bot /data
COPY --from=build /app .
USER bot
ENV DB_PATH=/data/albums.db
VOLUME /data
ENTRYPOINT ["dotnet", "nAlbum.dll"]