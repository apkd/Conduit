flake: { config, lib, pkgs, ... }:
let
  cfg = config.services.conduit;
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
  };

  config = lib.mkIf cfg.enable {
    home.packages = [ cfg.package ];
    systemd.user.services.conduit = {
      Unit = {
        Description = "Conduit Unity MCP server";
        After = [ "graphical-session.target" ];
        PartOf = [ "graphical-session.target" ];
      };
      Install.WantedBy = [ "graphical-session.target" ];
      Service = {
        ExecStart = "${lib.getExe cfg.package} --http --port ${toString cfg.port}";
        Environment = [ "PATH=${config.home.profileDirectory}/bin:/etc/profiles/per-user/${config.home.username}/bin:/run/current-system/sw/bin" ];
        Restart = "on-failure";
        RestartSec = 1;
      };
    };
  };
}
