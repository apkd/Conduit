{
  description = "Conduit Unity MCP server";

  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";

  outputs = { self, nixpkgs }: let
    system = "x86_64-linux";
    pkgs = nixpkgs.legacyPackages.${system};
  in {
    packages.${system} = rec {
      conduit = pkgs.callPackage ./nix/package.nix { };
      default = conduit;
    };
    homeManagerModules.default = import ./nix/home-manager.nix self;
  };
}
