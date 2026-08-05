# syntax=docker/dockerfile:1
#=====================================
#
     FROM ubuntu:22.04 AS builder
#
#=====================================
ARG DEBIAN_FRONTEND=noninteractive
ARG UNITY_VERSION
ARG PROJECT
ARG PROJECT_MANIFEST=test
ARG CONDUIT_RUNTIME_DEFINE=1

ENV INSTALL_ROOT=/opt/unity \
    UNITY_HOME=/opt/unity \
    UNITY_VERSION=${UNITY_VERSION} \
    PROJECT=${PROJECT} \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
    UNITY_THISISABUILDMACHINE=1 \
    PATH="/usr/local/bin:${PATH}"

RUN \
--mount=type=cache,target=/var/cache/apt,id=apt-cache-builder,sharing=locked \
--mount=type=cache,target=/var/lib/apt/lists,id=apt-lists-builder,sharing=locked \
--mount=type=bind,source=.github/workflows/test-manifest.json,target=/tmp/test-manifest.json,readonly \
--mount=type=bind,source=.github/workflows/player-manifest.json,target=/tmp/player-manifest.json,readonly \
--mount=type=bind,source=.github/workflows/consumer-manifest.json,target=/tmp/consumer-manifest.json,readonly \
\
#========================
<<'BUILDER_SETUP_SCRIPT'
#========================

set -eux

cp "/tmp/${PROJECT_MANIFEST}-manifest.json" /tmp/manifest.json

# debian nodoc
echo 'path-exclude /usr/share/man/*'               >  /etc/dpkg/dpkg.cfg.d/01_nodoc
echo 'path-exclude /usr/share/doc/*'               >> /etc/dpkg/dpkg.cfg.d/01_nodoc
echo 'path-exclude /usr/share/info/*'              >> /etc/dpkg/dpkg.cfg.d/01_nodoc
echo 'path-exclude /usr/share/locale/*'            >> /etc/dpkg/dpkg.cfg.d/01_nodoc
echo 'path-include /usr/share/locale/locale.alias' >> /etc/dpkg/dpkg.cfg.d/01_nodoc

# core runtime unity editor deps
apt-get update -o Acquire::Retries=10 -qq
apt-get install -y --no-install-recommends \
  tini binutils ca-certificates xz-utils axel xvfb libgtk-3-0 libglu1-mesa \
  libxrandr2 libxinerama1 libxcursor1 libxi6 libxss1 libnss3 libgbm1 fonts-dejavu-core
if ! dpkg -s libasound2 >/dev/null 2>&1 && ! dpkg -s libasound2t64 >/dev/null 2>&1; then
  apt-get install -y --no-install-recommends libasound2t64 || apt-get install -y --no-install-recommends libasound2
fi
if ! dpkg -s libcups2 >/dev/null 2>&1 && ! dpkg -s libcups2t64 >/dev/null 2>&1; then
  apt-get install -y --no-install-recommends libcups2t64 || apt-get install -y --no-install-recommends libcups2
fi

# clear apt lists
rm -rf /var/lib/apt/lists/*

download_with_axel() {
  local output="$1"
  local url="$2"
  shift 2

  local attempt=1
  local delay=2
  while [ "$attempt" -le 5 ]; do
    if axel -q -T 30 "$@" -o "$output" "$url"; then
      return
    fi

    # axel can resume only when the partial download has its matching state file.
    if [ -e "$output" ] && [ ! -e "${output}.st" ]; then
      rm -f "$output"
    fi
    if [ "$attempt" -eq 5 ]; then
      echo "Download failed after 5 attempts: $url" >&2
      return 1
    fi

    echo "Download failed; retrying in ${delay}s: $url" >&2
    sleep "$delay"
    attempt=$((attempt + 1))
    delay=$((delay * 2))
  done
}

# install libssl1.1 for older unity versions
# https://discussions.unity.com/t/workaround-for-libssl-issue-on-ubuntu-22-04/879165
# https://stackoverflow.com/questions/73378850/how-can-i-install-unity-hub-on-ubuntu-22-04
download_with_axel libssl1.1_1.1.1f-1ubuntu2.24_amd64.deb http://archive.ubuntu.com/ubuntu/pool/main/o/openssl/libssl1.1_1.1.1f-1ubuntu2.24_amd64.deb
dpkg -i libssl1.1_1.1.1f-1ubuntu2.24_amd64.deb
rm -f libssl1.1_1.1.1f-1ubuntu2.24_amd64.deb

DEST="${INSTALL_ROOT}/${UNITY_VERSION}"
mkdir -p "$DEST"
TMPHTML="/tmp/unity_whats_new.html"
download_with_axel "$TMPHTML" "https://unity.com/releases/editor/whats-new/${UNITY_VERSION}"

download_and_extract() {
  local url="$1"
  local tarball="$2"
  test -n "$url"
  echo "Downloading: $url"
  download_with_axel "$tarball" "$url" -n 16
  xz -T0 -dc "$tarball" | tar -x --no-same-owner --no-same-permissions --delay-directory-restore --no-xattrs --no-acls --no-selinux -C "$DEST"
  rm -f "$tarball"
}

EDITOR_URL="$( \
  tr '\n' ' ' < "$TMPHTML" \
  | grep -oE "https?://(download\.unity3d\.com/download_unity|beta\.unity3d\.com/download)/[^\"']+/LinuxEditorInstaller/Unity-${UNITY_VERSION}\.tar\.xz" \
  | head -n1 \
)"
REV=""
if [ -z "$EDITOR_URL" ]; then
  ARCHIVE_HTML="/tmp/unity_archive.html"
  download_with_axel "$ARCHIVE_HTML" "https://unity.com/releases/editor/archive"
  REV="$( \
    tr '\n' ' ' < "$ARCHIVE_HTML" \
    | grep -oE "unityhub://${UNITY_VERSION}/[0-9a-f]+" \
    | head -n1 \
    | sed -nE 's#^unityhub://[^/]+/([0-9a-f]+)$#\1#p' \
  )"
  test -n "$REV"
  EDITOR_URL="https://download.unity3d.com/download_unity/${REV}/LinuxEditorInstaller/Unity-${UNITY_VERSION}.tar.xz"
  rm -f "$ARCHIVE_HTML"
fi
test -n "$EDITOR_URL"

# extract revision
if [ -z "$REV" ]; then
  REV="$(printf '%s' "$EDITOR_URL" | sed -nE 's#.*/(download_unity|download)/([^/]+)/.*#\2#p')"
fi
test -n "$REV"
echo "Downloading: $EDITOR_URL (revision: $REV)"

download_and_extract "$EDITOR_URL" "/tmp/unity_editor.tar.xz"

# seed the generated project with settings from this exact editor version
PROJECT_TEMPLATE="$(find "$DEST/Editor/Data/Resources/PackageManager/ProjectTemplates" -maxdepth 1 -type f -name '*.tgz' -print -quit)"
test -n "$PROJECT_TEMPLATE"
tar -xOzf "$PROJECT_TEMPLATE" 'package/ProjectData~/ProjectSettings/ProjectSettings.asset' > /tmp/ProjectSettings.asset
case "$CONDUIT_RUNTIME_DEFINE" in
  0) ;;
  1)
    grep -q '^  scriptingDefineSymbols: {}$' /tmp/ProjectSettings.asset
    sed -i 's/^  scriptingDefineSymbols: {}$/  scriptingDefineSymbols:\n    Standalone: CONDUIT_INCLUDE_IN_DEBUG_BUILDS/' /tmp/ProjectSettings.asset
    ;;
  *)
    echo "CONDUIT_RUNTIME_DEFINE must be 0 or 1." >&2
    exit 1
    ;;
esac

# strip editor
rm -f "$TMPHTML"
rm -rf "$DEST/Editor/libumbraoptimizer64.*" || true
rm -rf "$DEST/Editor/libembree.*" || true
rm -rf "$DEST/Editor/libRadeonRays.*" || true
rm -rf "$DEST/Editor/libRL.*" || true
rm -rf "$DEST/Editor/Unity_s.debug" || true
rm -rf "$DEST/Editor/Data/Tools/usymtool" || true
rm -rf "$DEST/Editor/Data/Tools/macosx" || true
rm -rf "$DEST/Editor/Data/Tools/libembree.*" || true
rm -rf "$DEST/Editor/Data/Tools/libdxcompiler.*" || true
rm -rf "$DEST/Editor/Data/Tools/libUnityDenoisingPlugin.so" || true
rm -rf "$DEST/Editor/Data/Tools/libRadeonRays.*" || true
rm -rf "$DEST/Editor/Data/Tools/libRL.*" || true
rm -rf "$DEST/Editor/Data/Tools/libOpenImageDenoise.*" || true
rm -rf "$DEST/Editor/Data/Tools/astcenc-sse42.so" || true
rm -rf "$DEST/Editor/Data/Tools/astcenc-sse2.so" || true
rm -rf "$DEST/Editor/Data/Tools/astcenc-avx2.so" || true
rm -rf "$DEST/Editor/Data/Tools/VersionControl" || true
rm -rf "$DEST/Editor/Data/Tools/PVRTexTool" || true
rm -rf "$DEST/Editor/Data/Tools/LightBaker" || true
rm -rf "$DEST/Editor/Data/Tools/*_s.debug" || true
rm -rf "$DEST/Editor/Data/Resources/PackageManager/ProjectTemplates" || true
rm -rf "$DEST/Editor/Data/Resources/PackageManager/PackageTemplates" || true
rm -rf "$DEST/Editor/Data/Resources/PackageManager/Editor/*.tgz" || true
rm -rf "$DEST/Editor/Data/Resources/PackageManager/Diagnostics" || true
rm -rf "$DEST/Editor/Data/Resources/PackageManager/BuiltInPackages/com.unity.visualeffectgraph" || true
rm -rf "$DEST/Editor/Data/Resources/PackageManager/BuiltInPackages/com.unity.shaderanalysis" || true
rm -rf "$DEST/Editor/Data/Resources/PackageManager/BuiltInPackages/com.unity.rendering.denoising" || true
rm -rf "$DEST/Editor/Data/Resources/PackageManager/BuiltInPackages/com.unity.render-pipelines.high-definition-config" || true
rm -rf "$DEST/Editor/Data/Resources/PackageManager/BuiltInPackages/com.unity.render-pipelines.high-definition" || true
rm -rf "$DEST/Editor/Data/Resources/PackageManager/BuiltInPackages/com.unity.path-tracing" || true
rm -rf "$DEST/Editor/Data/Resources/PackageManager/BuiltInPackages/com.unity.multiplayer.center" || true
rm -rf "$DEST/Editor/Data/Resources/PackageManager/BuiltInPackages/com.unity.2d.tilemap" || true
rm -rf "$DEST/Editor/Data/Resources/PackageManager/BuiltInPackages/com.unity.2d.sprite" || true
rm -rf "$DEST/Editor/Data/Resources/OpenRL" || true
rm -rf "$DEST/Editor/Data/Resources/GI" || true
rm -rf "$DEST/Editor/Data/MonoEmbedRuntime" || true
rm -rf "$DEST/Editor/BugReporter" || true
rm -rf "$DEST/Editor/*_s.debug" || true
find "$DEST/Editor/Data/Resources/PackageManager/Editor/" ! -name 'manifest.json' -type f -exec rm -f {} + || true
find "$DEST" -type f \( -name '*.mdb' -o -name '*.dbg' \) -delete || true
find "$DEST" -type f \( -name '*.la' -o -name '*.pdb' \) -delete || true
find "$DEST" -type f -name '*.a' \
  ! -path "$DEST/Editor/Data/Tools/Burst/Client/bcl/libs/*" \
  -delete || true
find "$DEST" -type f -name '*.so' -exec strip --strip-unneeded {} + 2>/dev/null || true
strip --strip-unneeded "$DEST/Editor/Unity" 2>/dev/null || true
test -x "${DEST}/Editor/Unity"

# copy manifest.json
mkdir -p "${PROJECT}/Packages" "${PROJECT}/ProjectSettings" "${PROJECT}/Assets"
cp /tmp/manifest.json "${PROJECT}/Packages/manifest.json"
cp /tmp/ProjectSettings.asset "${PROJECT}/ProjectSettings/ProjectSettings.asset"

# create project.json
printf '%s\n' \
  "m_EditorVersion: ${UNITY_VERSION}" \
  "m_EditorVersionWithRevision: ${UNITY_VERSION} (${REV})" \
  > "${PROJECT}/ProjectSettings/ProjectVersion.txt"

# disable Unity Connect
cat > "${PROJECT}/ProjectSettings/UnityConnectSettings.asset" <<'YAML'
%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!310 &1
UnityConnectSettings:
  m_ObjectHideFlags: 0
  serializedVersion: 1
  m_Enabled: 0
  CrashReportingSettings:
    m_Enabled: 0
    m_LogBufferSize: 10
    m_CaptureEditorExceptions: 0
  UnityPurchasingSettings:
    m_Enabled: 0
  UnityAnalyticsSettings:
    m_Enabled: 0
    m_InitializeOnStartup: 0
  UnityAdsSettings:
    m_Enabled: 0
    m_InitializeOnStartup: 0
  PerformanceReportingSettings:
    m_Enabled: 0
YAML

apt-get purge --autoremove -y xz-utils binutils axel

install -d /usr/local/bin

printf '%s\n' '#!/usr/bin/env bash' \
  'set -euo pipefail' \
  'Xvfb :99 -screen 0 1024x768x24 > /dev/null 2>&1 &' \
  'xvfb_pid=$!' \
  "trap 'kill \$xvfb_pid 2>/dev/null || true; wait \$xvfb_pid 2>/dev/null || true' EXIT" \
  'export DISPLAY=:99' \
  'exec "$@"' \
  > /usr/local/bin/with-xvfb && chmod +x /usr/local/bin/with-xvfb

cat > /usr/local/bin/unity-editor <<'UNITY_EDITOR'
#!/usr/bin/env bash
set -euo pipefail

if [[ "${CONDUIT_XVFB_READY:-}" != 1 ]]; then
  export CONDUIT_XVFB_READY=1
  exec with-xvfb "$0" "$@"
fi

editor_path="${INSTALL_ROOT}/${UNITY_VERSION}/Editor/Unity"
: "${HOME:=/root}"
: "${XDG_CACHE_HOME:=$HOME/.cache}"
: "${XDG_CONFIG_HOME:=$HOME/.config}"
: "${XDG_DATA_HOME:=$HOME/.local/share}"
mkdir -p \
  "${XDG_CACHE_HOME}/unity3d" \
  "${XDG_CONFIG_HOME}/unity3d" \
  "${XDG_DATA_HOME}/unity3d"
ulimit -n 65535
ulimit -u 16384

login=0
arguments=()
for argument in "$@"; do
  if [[ "$argument" == "-login" ]]; then
    login=1
  else
    arguments+=("$argument")
  fi
done

editor_arguments=(
  -silent-crashes
  -batchmode
  -nographics
  -logFile -
  "${arguments[@]}"
)
if (( ! login )); then
  exec "$editor_path" "${editor_arguments[@]}"
fi

serial="${UNITY_SERIAL:-}"
username="${UNITY_USER:-}"
password="${UNITY_PASSWORD:-}"
[[ ! -f /run/secrets/unity_serial ]] || serial="$(</run/secrets/unity_serial)"
[[ ! -f /run/secrets/unity_user ]] || username="$(</run/secrets/unity_user)"
[[ ! -f /run/secrets/unity_password ]] || password="$(</run/secrets/unity_password)"
: "${serial:?UNITY serial is required}"
: "${username:?UNITY username is required}"
: "${password:?UNITY password is required}"

login_arguments=(
  -serial "$serial"
  -username "$username"
  -password "$password"
)
log_path="$(mktemp)"
trap 'rm -f "$log_path"' EXIT

# unity occasionally rejects a valid token before licensing starts. a fresh process
# recovers, while matching both messages prevents retries after ordinary build failures.
is_retryable_license_failure() {
  grep -Fq "Access token is unavailable; failed to update" "$log_path" &&
    grep -Fq "Failed to activate entitlement license" "$log_path"
}

max_attempts=4
for ((attempt = 1; attempt <= max_attempts; ++attempt)); do
  : > "$log_path"
  set +e
  "$editor_path" \
    "${login_arguments[@]}" \
    "${editor_arguments[@]}" \
    2>&1 | tee "$log_path"
  status=${PIPESTATUS[0]}
  set -e

  if (( status == 0 )); then
    exit 0
  fi
  if (( attempt == max_attempts )) || ! is_retryable_license_failure; then
    exit "$status"
  fi

  delay=$((120 << (attempt - 1)))
  echo "Unity license activation failed; retrying in ${delay}s ($((attempt + 1))/${max_attempts})..." >&2
  sleep "$delay"
done
UNITY_EDITOR
chmod +x /usr/local/bin/unity-editor

rm -rf "/root/.cache/unity3d" "/root/.config/unity3d" "/root/.local/share/unity3d" "${PROJECT}/Temp" || true

#=====================
#   (END OF THE)
BUILDER_SETUP_SCRIPT
#=====================

#=====================================
#
          FROM ubuntu:22.04
#
#=====================================
ARG DEBIAN_FRONTEND=noninteractive
ARG UNITY_VERSION
ARG PROJECT

ENV INSTALL_ROOT=/opt/unity \
    UNITY_HOME=/opt/unity \
    UNITY_VERSION=${UNITY_VERSION} \
    PROJECT=${PROJECT} \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
    UNITY_THISISABUILDMACHINE=1 \
    PATH="/usr/local/bin:${PATH}"

RUN \
--mount=type=cache,target=/var/cache/apt,id=apt-cache-runtime,sharing=locked \
--mount=type=cache,target=/var/lib/apt/lists,id=apt-lists-runtime,sharing=locked \
\
#==========================
<<'CONTAINER_SETUP_SCRIPT'
#==========================

set -eux

# debian nodoc
echo 'path-exclude /usr/share/man/*'               >  /etc/dpkg/dpkg.cfg.d/01_nodoc
echo 'path-exclude /usr/share/doc/*'               >> /etc/dpkg/dpkg.cfg.d/01_nodoc
echo 'path-exclude /usr/share/info/*'              >> /etc/dpkg/dpkg.cfg.d/01_nodoc
echo 'path-exclude /usr/share/locale/*'            >> /etc/dpkg/dpkg.cfg.d/01_nodoc
echo 'path-include /usr/share/locale/locale.alias' >> /etc/dpkg/dpkg.cfg.d/01_nodoc

apt-get update -o Acquire::Retries=10 -qq
apt-get install -y --no-install-recommends \
    tini ca-certificates xvfb libgtk-3-0 libglu1-mesa libxrandr2 \
    libxinerama1 libxcursor1 libxi6 libxss1 libnss3 libgbm1 fonts-dejavu-core
if ! dpkg -s libasound2 >/dev/null 2>&1 && ! dpkg -s libasound2t64 >/dev/null 2>&1; then
  apt-get install -y --no-install-recommends libasound2t64 || apt-get install -y --no-install-recommends libasound2
fi
if ! dpkg -s libcups2 >/dev/null 2>&1 && ! dpkg -s libcups2t64 >/dev/null 2>&1; then
  apt-get install -y --no-install-recommends libcups2t64 || apt-get install -y --no-install-recommends libcups2
fi
rm -rf /var/lib/apt/lists/*

#======================
#    (END OF THE)
CONTAINER_SETUP_SCRIPT
#======================

COPY --link --from=builder /usr/lib/x86_64-linux-gnu/libssl.so.1.1 /usr/lib/x86_64-linux-gnu/libcrypto.so.1.1 /usr/lib/x86_64-linux-gnu/
COPY --link --from=builder /opt/unity/${UNITY_VERSION} /opt/unity/${UNITY_VERSION}
COPY --link --from=builder /usr/local/bin/with-xvfb /usr/local/bin/with-xvfb
COPY --link --from=builder /usr/local/bin/unity-editor /usr/local/bin/unity-editor
COPY --link --from=builder ${PROJECT} ${PROJECT}

ENTRYPOINT ["/usr/bin/tini","--"]
CMD ["bash","-lc","unity-editor -h || true"]
