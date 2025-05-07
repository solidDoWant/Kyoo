# FROM golang:1.23 as build
FROM debian:trixie-slim AS build
# those were copied from https://github.com/docker-library/golang/blob/master/Dockerfile-linux.template
ENV GOTOOLCHAIN=local
ENV GOPATH=/go
ENV PATH=$GOPATH/bin:/usr/local/go/bin:$PATH
RUN set -eux; \
	apt-get update; \
	apt-get install -y --no-install-recommends \
	ca-certificates openssl \
	golang\
	g++ \
	gcc \
	libc6-dev \
	make \
	pkg-config

# https://github.com/golang/go/issues/54400
ENV SSL_CERT_DIR=/etc/ssl/certs
RUN update-ca-certificates

RUN apt-get update \
	&& apt-get install --no-install-recommends --no-install-suggests -y \
	ffmpeg libavformat-dev libavutil-dev libswscale-dev \
	&& apt-get clean autoclean -y \
	&& apt-get autoremove -y
WORKDIR /app
COPY go.mod go.sum ./
RUN go mod download
COPY . .
RUN go build -o ./transcoder

# debian is required for nvidia hardware acceleration
# we use trixie (debian's testing because ffmpeg on latest is v5 and we need v6)
# https://packages.debian.org/bookworm/ffmpeg for version tracking
FROM debian:trixie-slim

# read target arch from buildx or default to amd64 if using legacy builder.
ARG TARGETARCH
ENV TARGETARCH=${TARGETARCH:-amd64}
RUN echo $TARGETARCH
RUN sed -i -e's/ main/ main contrib non-free/g' /etc/apt/sources.list.d/debian.sources \
	&& set -x \
	# Install curl to assist with setup of the intel apt repo
	&& apt-get update \
	# && apt-get install --no-install-recommends --no-install-suggests -y curl ca-certificates \
	# Setup the intel apt repo if on amd64
	# Intel repos do not currently support Debian, only Ubuntu
	# && if [ "$TARGETARCH" = "amd64" ]; then \
	# 	curl -fsSL https://repositories.intel.com/graphics/intel-graphics.key > /usr/share/keyrings/intel-graphics.gpg \
	# 	&& . /etc/os-release \
	# 	&& echo "deb [arch=amd64 signed-by=/usr/share/keyrings/intel-graphics.gpg] https://repositories.intel.com/gpu/${ID}/ ${VERSION_CODENAME} unified" > /etc/apt/sources.list.d/intel-graphics.list; \
	# 	echo "deb https://repositories.intel.com/graphics/debian/ $(lsb_release -cs) main" > /etc/apt/sources.list.d/intel-graphics.list; \
	# fi \
	&& apt-get install --no-install-recommends --no-install-suggests -y \
	# runtime dependencies
	ffmpeg \
	# hwaccel dependencies
	vainfo mesa-va-drivers \
	# intel hwaccel dependencies, not available everywhere
	# Roughly follow https://github.com/blakeblackshear/frigate/blob/dev/docker/main/install_deps.sh#L59-L79 with the
	# libvpl packages required by ffmpeg here: https://trac.ffmpeg.org/wiki/Hardware/QuickSync
	$([ " $TARGETARCH" = " amd64" ] && echo "intel-media-va-driver-non-free i965-va-driver-shaders libmfx-gen1.2 libvpl2 libigfxcmrt7") \
	# CA certificates for HTTPS to S3 buckets
	ca-certificates \
	# Debugging \
	strace \
	&& apt-get clean autoclean -y \
	&& apt-get autoremove -y \
	&& rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/transcoder /app/transcoder
COPY ./migrations /app/migrations

# flags for nvidia acceleration on docker < 25.0
ENV NVIDIA_VISIBLE_DEVICES="all"
ENV NVIDIA_DRIVER_CAPABILITIES="all"

EXPOSE 7666
CMD ["./transcoder"]
