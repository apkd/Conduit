# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0.201-noble

ARG TARGETARCH
ARG UMU_LAUNCHER_VERSION=1.4.4
ARG UMU_LAUNCHER_PYTHON_SHA256=86b7a234f77fbcd13699654656192a12ed3852ec2bcc721506ae4f91436b3793
ARG UMU_LAUNCHER_SHA256=8d11aa5bf0edaa988a4cbb04a1414a3d2249128913e6130e6a2a8528c9ef31b1
ARG UMU_PROTON_VERSION=UMU-Proton-10.0-4
ARG UMU_PROTON_SHA512=5e1e7c99e773b4d0757f90075c5ff7f5282bedd13a694fc0414486122e68c9452d3fb971f32ab32704d4463c9622475a44e58fbd014069aa1e84e27b4a4ed7e5
ARG STEAM_RUNTIME_VERSION=3.0.20260805.254768
ARG STEAM_RUNTIME_SHA256=e264f0639ab775338311036f207b35cebe99bc417b016b53931ebca8b30b3d94

ENV DEBIAN_FRONTEND=noninteractive \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1

RUN \
--mount=type=cache,target=/var/cache/apt,id=apt-cache-proton,sharing=locked \
--mount=type=cache,target=/var/lib/apt/lists,id=apt-lists-proton,sharing=locked \
<<'INSTALL_PACKAGES'
set -eux
test "$TARGETARCH" = amd64

dpkg --add-architecture i386
apt-get update -o Acquire::Retries=10 -qq
apt-get install -y --no-install-recommends \
  ca-certificates curl file jq locales procps util-linux xauth xvfb xz-utils \
  libgtk-3-0 libglu1-mesa libxrandr2 libxinerama1 libxcursor1 libxi6 libxss1 \
  libnss3 libgbm1 libgl1-mesa-dri libgl1-mesa-dri:i386 libvulkan1 libvulkan1:i386 \
  mesa-vulkan-drivers mesa-vulkan-drivers:i386

python_package="python3-umu-launcher_${UMU_LAUNCHER_VERSION}-1_amd64_ubuntu-noble.deb"
launcher_package="umu-launcher_${UMU_LAUNCHER_VERSION}-1_all_ubuntu-noble.deb"
base_url="https://github.com/Open-Wine-Components/umu-launcher/releases/download/${UMU_LAUNCHER_VERSION}"

curl -fsSL --retry 8 --retry-all-errors --connect-timeout 30 \
  "$base_url/$python_package" -o "/tmp/$python_package"
curl -fsSL --retry 8 --retry-all-errors --connect-timeout 30 \
  "$base_url/$launcher_package" -o "/tmp/$launcher_package"
printf '%s  %s\n' "$UMU_LAUNCHER_PYTHON_SHA256" "/tmp/$python_package" | sha256sum -c -
printf '%s  %s\n' "$UMU_LAUNCHER_SHA256" "/tmp/$launcher_package" | sha256sum -c -
apt-get install -y --no-install-recommends "/tmp/$python_package" "/tmp/$launcher_package"
locale-gen en_US.UTF-8

rm -f "/tmp/$python_package" "/tmp/$launcher_package"
rm -rf /var/lib/apt/lists/*

install -d -o ubuntu -g ubuntu -m 700 /home/ubuntu/runtime /home/ubuntu/tmp
INSTALL_PACKAGES

ENV HOME=/home/ubuntu \
    LANG=en_US.UTF-8 \
    LC_ALL=en_US.UTF-8 \
    XDG_DATA_HOME=/home/ubuntu/.local/share \
    XDG_CACHE_HOME=/home/ubuntu/.cache \
    XDG_RUNTIME_DIR=/home/ubuntu/runtime \
    TMPDIR=/home/ubuntu/tmp \
    WINEPREFIX=/home/ubuntu/prefix \
    GAMEID=umu-conduit-ci \
    STORE=none \
    RUNTIMEPATH=steamrt3 \
    UMU_RUNTIME_UPDATE=0 \
    PROTONPATH=/home/ubuntu/.local/share/Steam/compatibilitytools.d/${UMU_PROTON_VERSION}

USER ubuntu
WORKDIR /home/ubuntu

RUN <<'INSTALL_RUNTIME'
set -eux

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
compatibility_tools="$XDG_DATA_HOME/Steam/compatibilitytools.d"
runtime="$XDG_DATA_HOME/umu/steamrt3"
mkdir -p "$compatibility_tools" "$runtime"

proton_archive="$work/${UMU_PROTON_VERSION}.tar.gz"
curl -fsSL --retry 8 --retry-all-errors --connect-timeout 30 \
  "https://github.com/Open-Wine-Components/umu-proton/releases/download/${UMU_PROTON_VERSION}/${UMU_PROTON_VERSION}.tar.gz" \
  -o "$proton_archive"
printf '%s  %s\n' "$UMU_PROTON_SHA512" "$proton_archive" | sha512sum -c -
tar -xzf "$proton_archive" -C "$compatibility_tools"

runtime_archive="$work/SteamLinuxRuntime_sniper.tar.xz"
curl -fsSL --retry 8 --retry-all-errors --connect-timeout 30 \
  "https://repo.steampowered.com/steamrt3/images/${STEAM_RUNTIME_VERSION}/SteamLinuxRuntime_sniper.tar.xz" \
  -o "$runtime_archive"
printf '%s  %s\n' "$STEAM_RUNTIME_SHA256" "$runtime_archive" | sha256sum -c -
tar -xJf "$runtime_archive" -C "$work"
cp -a "$work/SteamLinuxRuntime_sniper/." "$runtime/"
mv "$runtime/_v2-entry-point" "$runtime/umu"

# mark the runtime installed only after Valve's verifier accepts the extracted platform.
platform="$(find "$runtime" -mindepth 1 -maxdepth 1 -type d -name 'sniper_platform_*' -print -quit)"
test -n "$platform"
test -x "$PROTONPATH/proton"
grep -Eq 'require_tool_appid[^0-9]*1628350' "$PROTONPATH/toolmanifest.vdf"
"$runtime/pressure-vessel/bin/pv-verify" --quiet --minimized-runtime "$platform/files"
printf 'ok\n' > "$runtime/.installed.ok"

# umu creates this pass-through shim on first launch; bake it because runtime updates are disabled.
cat > "$XDG_DATA_HOME/umu/umu-shim" <<'SHIM'
#!/bin/sh
exec "$@"
SHIM
chmod 700 "$XDG_DATA_HOME/umu/umu-shim"
umu-run --version
INSTALL_RUNTIME

WORKDIR /mnt/conduit
