namespace Conduit;

public sealed partial class UnityProjectEnvironmentProbeTests
{
    [Test]
    public async Task ResolveExecutablePathPrefersPrimaryPath()
    {
        var primaryDirectory = Directory.CreateTempSubdirectory("conduit-primary-path-");
        var fallbackDirectory = Directory.CreateTempSubdirectory("conduit-fallback-path-");
        try
        {
            var primaryExecutable = Path.Combine(primaryDirectory.FullName, "hyprctl");
            await File.WriteAllTextAsync(primaryExecutable, string.Empty);
            await File.WriteAllTextAsync(Path.Combine(fallbackDirectory.FullName, "hyprctl"), string.Empty);

            var resolved = WaylandCompositorDiscovery.ResolveExecutablePath(
                "hyprctl",
                primaryDirectory.FullName,
                fallbackDirectory.FullName
            );

            await Assert.That(resolved).IsEqualTo(primaryExecutable);
        }
        finally
        {
            Directory.Delete(primaryDirectory.FullName, recursive: true);
            Directory.Delete(fallbackDirectory.FullName, recursive: true);
        }
    }

    [Test]
    public async Task TryFindSwaySocketPrefersExplicitSocket()
    {
        var runtimeDirectory = Directory.CreateTempSubdirectory("conduit-sway-runtime-");
        try
        {
            var discoveredSocket = Path.Combine(runtimeDirectory.FullName, "sway-ipc.1000.1.sock");
            var explicitSocket = Path.Combine(runtimeDirectory.FullName, "explicit.sock");
            await File.WriteAllTextAsync(discoveredSocket, string.Empty);
            await File.WriteAllTextAsync(explicitSocket, string.Empty);

            var socket = WaylandCompositorDiscovery.TryFindSwaySocket(
                runtimeDirectory.FullName,
                explicitSocket
            );

            await Assert.That(socket).IsEqualTo(explicitSocket);
        }
        finally
        {
            Directory.Delete(runtimeDirectory.FullName, recursive: true);
        }
    }

    [Test]
    public async Task TryFindSwaySocketDiscoversRuntimeSocket()
    {
        var runtimeDirectory = Directory.CreateTempSubdirectory("conduit-sway-runtime-");
        try
        {
            var socketPath = Path.Combine(runtimeDirectory.FullName, "sway-ipc.1000.1.sock");
            await File.WriteAllTextAsync(socketPath, string.Empty);

            var socket = WaylandCompositorDiscovery.TryFindSwaySocket(runtimeDirectory.FullName);

            await Assert.That(socket).IsEqualTo(socketPath);
        }
        finally
        {
            Directory.Delete(runtimeDirectory.FullName, recursive: true);
        }
    }

    [Test]
    public async Task TryFindNiriSocketPrefersExplicitSocket()
    {
        var runtimeDirectory = Directory.CreateTempSubdirectory("conduit-niri-runtime-");
        try
        {
            var discoveredSocket = Path.Combine(runtimeDirectory.FullName, "niri.wayland-1.1000.sock");
            var explicitSocket = Path.Combine(runtimeDirectory.FullName, "explicit.sock");
            await File.WriteAllTextAsync(discoveredSocket, string.Empty);
            await File.WriteAllTextAsync(explicitSocket, string.Empty);

            var socket = WaylandCompositorDiscovery.TryFindNiriSocket(
                runtimeDirectory.FullName,
                "wayland-1",
                explicitSocket
            );

            await Assert.That(socket).IsEqualTo(explicitSocket);
        }
        finally
        {
            Directory.Delete(runtimeDirectory.FullName, recursive: true);
        }
    }

    [Test]
    public async Task TryFindNiriSocketDiscoversWaylandDisplaySocket()
    {
        var runtimeDirectory = Directory.CreateTempSubdirectory("conduit-niri-runtime-");
        try
        {
            var socketPath = Path.Combine(runtimeDirectory.FullName, "niri.wayland-1.1000.sock");
            await File.WriteAllTextAsync(socketPath, string.Empty);
            await File.WriteAllTextAsync(Path.Combine(runtimeDirectory.FullName, "niri.wayland-2.1000.sock"), string.Empty);

            var socket = WaylandCompositorDiscovery.TryFindNiriSocket(
                runtimeDirectory.FullName,
                "wayland-1"
            );

            await Assert.That(socket).IsEqualTo(socketPath);
        }
        finally
        {
            Directory.Delete(runtimeDirectory.FullName, recursive: true);
        }
    }

    [Test]
    public async Task TryInferHyprlandInstanceSignatureUsesSocketDirectory()
    {
        var runtimeDirectory = Directory.CreateTempSubdirectory("conduit-runtime-");
        try
        {
            var instanceDirectory = Directory.CreateDirectory(
                Path.Combine(runtimeDirectory.FullName, "hypr", "instance_1")
            );
            await File.WriteAllTextAsync(Path.Combine(instanceDirectory.FullName, ".socket.sock"), string.Empty);

            var signature = WaylandCompositorDiscovery.TryInferHyprlandInstanceSignature(
                runtimeDirectory.FullName
            );

            await Assert.That(signature).IsEqualTo("instance_1");
        }
        finally
        {
            Directory.Delete(runtimeDirectory.FullName, recursive: true);
        }
    }

    [Test]
    public async Task HasConduitPackageSignalReturnsFalseWhenPackageIsAbsent()
    {
        var projectPath = CreateTempProject();
        try
        {
            await Assert.That(UnityProjectPackageProbe.HasConduitPackageSignal(projectPath)).IsFalse();
        }
        finally
        {
            Directory.Delete(projectPath, recursive: true);
        }
    }

    [Test]
    public async Task HasConduitPackageSignalDetectsManifestDependency()
    {
        var projectPath = CreateTempProject();
        try
        {
            Directory.CreateDirectory(Path.Combine(projectPath, "Packages"));
            await File.WriteAllTextAsync(
                Path.Combine(projectPath, "Packages", "manifest.json"),
                """
                {
                  "dependencies": {
                    "dev.tryfinally.conduit": "https://github.com/apkd/Conduit.git?path=/Conduit.Unity#release"
                  }
                }
                """
            );
            await Assert.That(UnityProjectPackageProbe.HasConduitPackageSignal(projectPath)).IsTrue();
        }
        finally
        {
            Directory.Delete(projectPath, recursive: true);
        }
    }

    [Test]
    public async Task HasConduitPackageSignalDetectsEmbeddedPackage()
    {
        var projectPath = CreateTempProject();
        try
        {
            var embeddedPackagePath = Path.Combine(projectPath, "Packages", "dev.tryfinally.conduit");
            Directory.CreateDirectory(embeddedPackagePath);
            await File.WriteAllTextAsync(Path.Combine(embeddedPackagePath, "package.json"), "{}");
            await Assert.That(UnityProjectPackageProbe.HasConduitPackageSignal(projectPath)).IsTrue();
        }
        finally
        {
            Directory.Delete(projectPath, recursive: true);
        }
    }

}
