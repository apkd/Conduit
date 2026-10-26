flake: { config, lib, pkgs, ... }:
let
  cfg = config.services.conduit;
  escapeExecArgs = lib.concatMapStringsSep " " (arg:
    lib.replaceStrings [ "%" "$" ] [ "%%" "$$" ] (builtins.toJSON arg));
in {
  options.services.conduit = {
    enable = lib.mkEnableOption "the Conduit HTTP server";
    package = lib.mkOption {
      type = lib.types.package;
      default = flake.packages.${pkgs.stdenv.hostPlatform.system}.conduit;
      description = "Conduit package to run.";
    };
    port = lib.mkOption {
      type = lib.types.port;
      default = 5080;
      description = "HTTP port on 127.0.0.1.";
    };
    tokenFile = lib.mkOption {
      type = lib.types.nullOr lib.types.str;
      default = null;
      description = "Optional absolute runtime path to a bearer token file. Keep its contents outside the Nix store.";
    };
    tokenSha256File = lib.mkOption {
      type = lib.types.nullOr lib.types.str;
      default = null;
      description = "Optional absolute runtime path to a token's SHA-256 fingerprint file.";
    };
    protectProcess = lib.mkOption {
      type = lib.types.bool;
      default = false;
      description = "Protect the server process from memory inspection and disable .NET diagnostics on Linux.";
    };
  };

  config = lib.mkIf cfg.enable {
    assertions = [
      {
        assertion = cfg.tokenFile == null || cfg.tokenSha256File == null;
        message = "services.conduit accepts only one of tokenFile and tokenSha256File.";
      }
      {
        assertion = lib.all (path: path == null || lib.hasPrefix "/" path) [ cfg.tokenFile cfg.tokenSha256File ];
        message = "services.conduit token file paths must be absolute.";
      }
      {
        assertion = !cfg.protectProcess || pkgs.stdenv.hostPlatform.isLinux;
        message = "services.conduit.protectProcess requires Linux.";
      }
    ];
    home.packages = [ cfg.package ];
    systemd.user.services.conduit = {
      Unit = {
        Description = "Conduit Unity MCP server";
        After = [ "graphical-session.target" ];
        PartOf = [ "graphical-session.target" ];
      };
      Install.WantedBy = [ "graphical-session.target" ];
      Service = {
        ExecStart = escapeExecArgs ([
          (lib.getExe cfg.package) "--http" "--port" (toString cfg.port)
        ] ++ lib.optionals (cfg.tokenFile != null) [ "--token-file" cfg.tokenFile ]
          ++ lib.optionals (cfg.tokenSha256File != null) [ "--token-sha256-file" cfg.tokenSha256File ]
          ++ lib.optional cfg.protectProcess "--protect-process");
        Environment = [ "PATH=${config.home.profileDirectory}/bin:/etc/profiles/per-user/${config.home.username}/bin:/run/current-system/sw/bin" ]
          ++ lib.optional cfg.protectProcess "DOTNET_EnableDiagnostics=0";
        Restart = "on-failure";
        RestartSec = 1;
      };
    };
  };
}
