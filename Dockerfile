FROM docker.io/library/node:26.13.1-bookworm-slim@sha256:5df79989ed685e09f0a98019db4cebfb22fb9df1a53c0161664a011f7ff6b0f1 AS client
WORKDIR /build/web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0.404-noble@sha256:bef136a371963aaa46ec97882845ef6f54dec15728bb4b128b1c2a400ffbc9b4 AS publish
WORKDIR /build
COPY global.json Directory.Build.props Directory.Build.targets ./
COPY src/Mahjong.Rules/Mahjong.Rules.csproj src/Mahjong.Rules/packages.lock.json src/Mahjong.Rules/
COPY src/JadeCourt.Games/JadeCourt.Games.csproj src/JadeCourt.Games/packages.lock.json src/JadeCourt.Games/
COPY src/JadeCourt.Chess/JadeCourt.Chess.csproj src/JadeCourt.Chess/packages.lock.json src/JadeCourt.Chess/
COPY src/JadeCourt.Poker/JadeCourt.Poker.csproj src/JadeCourt.Poker/packages.lock.json src/JadeCourt.Poker/
COPY src/JadeCourt.Solo/JadeCourt.Solo.csproj src/JadeCourt.Solo/packages.lock.json src/JadeCourt.Solo/
COPY src/JadeCourt.Words/JadeCourt.Words.csproj src/JadeCourt.Words/packages.lock.json src/JadeCourt.Words/
COPY src/Mahjong.Server/Mahjong.Server.csproj src/Mahjong.Server/packages.lock.json src/Mahjong.Server/
RUN dotnet restore src/Mahjong.Server/Mahjong.Server.csproj -p:RestoreLockedMode=true

COPY src/ src/
COPY LICENSE ./
COPY scripts/licenses/ scripts/licenses/
COPY --from=client /build/web/dist/ web/dist/
ARG BUILD_VERSION=0.1.0
ARG SOURCE_COMMIT=unknown
RUN dotnet publish src/Mahjong.Server/Mahjong.Server.csproj -c Release --no-restore -o /out \
      -p:UseAppHost=false -p:ContinuousIntegrationBuild=true \
      -p:Version="$BUILD_VERSION" -p:InformationalVersion="$BUILD_VERSION+$SOURCE_COMMIT" \
      -p:IncludeSourceRevisionInInformationalVersion=false \
    && test -s /out/index.html && test -s /out/portal.html \
    && test -s /out/JadeCourt.Poker.dll && test -s /out/JadeCourt.Solo.dll && test -s /out/JadeCourt.Words.dll

FROM mcr.microsoft.com/dotnet/aspnet:10.0.15-noble@sha256:12b1135f5f8b3c0a214f2a680ff2033dff4f684f445d6c697d9524ceeb303586 AS aspnet

FROM aspnet AS stockfish
WORKDIR /stockfish
# Use only Noble's signed release pocket. Ship the matching source, including the packaging patches.
RUN set -eux; \
    printf '%s\n' \
      'deb [arch=amd64 signed-by=/usr/share/keyrings/ubuntu-archive-keyring.gpg] https://archive.ubuntu.com/ubuntu noble universe' \
      'deb-src [signed-by=/usr/share/keyrings/ubuntu-archive-keyring.gpg] https://archive.ubuntu.com/ubuntu noble universe' \
      > /etc/apt/sources.list.d/stockfish.list; \
    apt_noble() { apt-get -o Dir::Etc::sourcelist=sources.list.d/stockfish.list -o Dir::Etc::sourceparts=- "$@"; }; \
    apt_noble update; \
    chown _apt /stockfish; \
    apt_noble download stockfish=16-1build1; \
    apt_noble source --download-only stockfish=16-1build1; \
    printf '%s\n' \
      '909bfd433b049497f06ec3f63517deaf4e821acc138c974bb2a8af2bd58b073f  stockfish_16-1build1_amd64.deb' \
      'd5f19c6cc601e3205d711489ed00f9a30fce830162efd08bc705254830334eea  stockfish_16-1build1.dsc' \
      'a928bcf1ba7894a14ff15f78f2b4081396c5ed3cf5f2aab29b0827423fd883e8  stockfish_16.orig.tar.gz' \
      '9b7264396f097bdd77e9b3f72e12fddc92939f29ef725da8f1751aadf68e9d10  stockfish_16-1build1.debian.tar.xz' \
      | sha256sum -c -; \
    dpkg-deb -x stockfish_16-1build1_amd64.deb /engine; \
    install -d /engine/usr/share/doc/stockfish/source; \
    install -m 0644 stockfish_16-1build1.dsc stockfish_16.orig.tar.gz stockfish_16-1build1.debian.tar.xz \
      /engine/usr/share/doc/stockfish/source/

FROM aspnet AS runtime
ARG BUILD_VERSION=0.1.0
ARG SOURCE_COMMIT=unknown
LABEL org.opencontainers.image.title="Jade Court" \
      org.opencontainers.image.source="https://github.com/long2know/game-portal" \
      org.opencontainers.image.version="$BUILD_VERSION" \
      org.opencontainers.image.revision="$SOURCE_COMMIT"
# Keep /data on the legacy alias so existing Mahjong__DataDirectory overrides still work.
ENV ASPNETCORE_HTTP_PORTS=8080 \
    BASE_URL=/portal \
    Mahjong__DataDirectory=/data \
    Portal__Enabled=true \
    Portal__StockfishPath=/usr/games/stockfish
WORKDIR /app
RUN install -d -o app -g app -m 0700 /data /data/keys
COPY --from=stockfish /engine/usr/games/stockfish /usr/games/stockfish
COPY --from=stockfish /engine/usr/share/doc/stockfish/ /usr/share/doc/stockfish/
COPY --from=publish /out/ ./
USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=5s --start-period=20s --retries=3 \
  CMD ["bash", "-ec", "exec 3<>/dev/tcp/127.0.0.1/8080; printf 'GET %s/health/ready HTTP/1.1\\r\\nHost: localhost\\r\\nConnection: close\\r\\n\\r\\n' \"${BASE_URL%/}\" >&3; IFS=' ' read -r proto status rest <&3; test \"$status\" = 200"]
ENTRYPOINT ["dotnet", "Mahjong.Server.dll"]
