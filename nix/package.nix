{ lib, stdenvNoCC, stdenv, fetchurl, autoPatchelfHook, makeWrapper, openssl, bash, util-linux }:
let
  release = builtins.fromJSON (builtins.readFile ./release.json);
in stdenvNoCC.mkDerivation {
  pname = "conduit";
  inherit (release) version;
  src = fetchurl { inherit (release) url hash; };
  dontUnpack = true;
  dontStrip = true;  # stripping ELF sections also removes the appended .NET single-file bundle
  nativeBuildInputs = [ autoPatchelfHook makeWrapper ];
  buildInputs = [ (lib.getLib stdenv.cc.cc) ];
  runtimeDependencies = [ (lib.getLib openssl) ];
  installPhase = ''
    runHook preInstall
    install -Dm755 "$src" "$out/libexec/conduit"
    runHook postInstall
  '';
  postFixup = ''
    # preserve the executable name for System.CommandLine's command-name detection.
    mkdir -p "$out/bin"
    makeWrapper "$out/libexec/conduit" "$out/bin/conduit" --prefix PATH : ${lib.makeBinPath [ bash util-linux ]}
  '';
  meta = {
    description = "MCP server for Unity editors and development players";
    homepage = "https://github.com/apkd/Conduit";
    license = lib.licenses.mit;
    platforms = [ "x86_64-linux" ];
    mainProgram = "conduit";
    sourceProvenance = [ lib.sourceTypes.binaryNativeCode ];
  };
}
