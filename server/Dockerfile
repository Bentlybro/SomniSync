FROM rust:1-bookworm AS build
WORKDIR /src/server
COPY server/Cargo.toml server/Cargo.lock ./
COPY server/src ./src
RUN cargo build --locked --release

FROM debian:bookworm-slim
LABEL org.opencontainers.image.licenses="MPL-2.0" \
      org.opencontainers.image.source="https://github.com/Bentlybro/SomniSync"
RUN groupadd --system --gid 10001 somnisync \
    && useradd --system --uid 10001 --gid 10001 --home-dir /app somnisync \
    && mkdir -p /app/data \
    && chown -R somnisync:somnisync /app
COPY --from=build /src/server/target/release/somnium-relay /usr/local/bin/somnium-relay
COPY LICENSE NOTICE.md /app/
USER 10001:10001
WORKDIR /app
ENV SOMNISYNC_WS_BIND=0.0.0.0:9090 \
    SOMNISYNC_METRICS_BIND=0.0.0.0:9091 \
    SOMNISYNC_DATA_DIR=/app/data
EXPOSE 9090
VOLUME /app/data
CMD ["somnium-relay"]
