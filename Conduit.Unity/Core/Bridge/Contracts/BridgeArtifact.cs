#nullable enable

using System;
using System.IO;
using System.Security.Cryptography;

namespace Conduit
{
    [Serializable]
    sealed class BridgeArtifact
    {
        public string name = string.Empty;
        public string media_type = "application/octet-stream";
        public string sha256 = string.Empty;
        public long length;
        public string? relative_path;

        public string Name { get => name; set => name = value; }
        public string MediaType { get => media_type; set => media_type = value; }
        public string Sha256 { get => sha256; set => sha256 = value; }
        public long Length { get => length; set => length = value; }
        public string? RelativePath { get => relative_path; set => relative_path = value; }

        internal byte[]? Content { get; set; }
        internal string? ResolvedPath { get; set; }

        public static BridgeArtifact FromBytes(string name, string mediaType, byte[] bytes)
            => new()
            {
                name = name,
                media_type = mediaType,
                sha256 = ComputeSha256(bytes),
                length = bytes.LongLength,
                Content = bytes,
            };

        public static BridgeArtifact FromProjectFile(string name, string mediaType, string relativePath, byte[] bytes)
            => new()
            {
                name = name,
                media_type = mediaType,
                sha256 = ComputeSha256(bytes),
                length = bytes.LongLength,
                relative_path = relativePath.Replace(Path.DirectorySeparatorChar, '/'),
            };

        internal BridgeArtifact AsProjectFile(string relativePath)
            => new()
            {
                name = name,
                media_type = media_type,
                sha256 = sha256,
                length = length,
                relative_path = relativePath.Replace(Path.DirectorySeparatorChar, '/'),
            };

        internal void MaterializeInEndpoint(string endpointDirectory)
        {
            if (Content is not { } bytes)
                throw new InvalidOperationException($"Artifact '{name}' has no content to materialize.");

            Verify(bytes);
            var directory = Path.Combine(endpointDirectory, "artifacts");
            Directory.CreateDirectory(directory);
            RejectLink(directory);
            var fileName = sha256 + GetSafeExtension(name);
            var path = Path.Combine(directory, fileName);
            WriteAtomically(path, bytes, sha256);
            // endpoint-relative paths survive host/container path translation in pressure-vessel.
            relative_path = "artifacts/" + fileName;
            ResolvedPath = path;
            Content = null;
        }

        internal void ResolveInEndpoint(string endpointDirectory)
        {
            if (string.IsNullOrWhiteSpace(relative_path) || Path.IsPathRooted(relative_path))
                throw new InvalidDataException($"Artifact '{name}' must use an endpoint-relative path.");
            if (length < 0 || sha256.Length != 64)
                throw new InvalidDataException($"Artifact '{name}' has invalid verification metadata.");

            foreach (var character in sha256)
                if (!Uri.IsHexDigit(character))
                    throw new InvalidDataException($"Artifact '{name}' has an invalid SHA-256 value.");

            var fileName = sha256.ToLowerInvariant() + GetSafeExtension(name);
            var normalizedRelativePath = relative_path.Replace('/', Path.DirectorySeparatorChar);
            var expectedRelativePath = Path.Combine("artifacts", fileName);
            if (!string.Equals(
                    normalizedRelativePath,
                    expectedRelativePath,
                    StringComparison.Ordinal
                ))
                throw new InvalidDataException($"Artifact '{name}' has an unexpected endpoint path.");

            var artifactRoot = Path.GetFullPath(Path.Combine(endpointDirectory, "artifacts"));
            RejectLink(artifactRoot);
            ResolvedPath = Path.Combine(artifactRoot, fileName);
        }

        internal void ResolveInProject(string projectDirectory)
        {
            if (string.IsNullOrWhiteSpace(relative_path) || Path.IsPathRooted(relative_path))
                throw new InvalidDataException($"Artifact '{name}' must use a project-relative path.");

            var projectRoot = Path.GetFullPath(projectDirectory);
            var path = Path.GetFullPath(Path.Combine(projectRoot, relative_path));
            var normalized = Path.GetRelativePath(projectRoot, path);
            if (Path.IsPathRooted(normalized)
                || normalized == ".."
                || normalized.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException($"Artifact '{name}' escaped its Unity project.");

            ResolvedPath = path;
        }

        internal byte[] ReadVerified()
        {
            byte[] bytes;
            if (Content is { } content)
                bytes = content;
            else if (ResolvedPath is { } path)
            {
                RejectLink(path);
                bytes = File.ReadAllBytes(path);
            }
            else
                throw new InvalidOperationException($"Artifact '{name}' has no resolved content.");

            Verify(bytes);
            return bytes;
        }

        internal void Verify(byte[] bytes)
        {
            if (bytes.LongLength != length)
                throw new InvalidDataException($"Artifact '{name}' failed length verification.");
            if (!string.Equals(ComputeSha256(bytes), sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Artifact '{name}' failed SHA-256 verification.");
        }

        static string GetSafeExtension(string artifactName)
        {
            var extension = Path.GetExtension(Path.GetFileName(artifactName));
            if (extension.Length is 0 or > 16)
                return string.Empty;

            for (var index = 1; index < extension.Length; index++)
                if (!char.IsLetterOrDigit(extension[index]))
                    return string.Empty;

            return extension.ToLowerInvariant();
        }

        static void WriteAtomically(string path, byte[] bytes, string expectedSha256)
        {
            if (File.Exists(path))
            {
                VerifyFile(path, bytes.LongLength, expectedSha256);
                return;
            }

            var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporaryPath, bytes);
                try
                {
                    File.Move(temporaryPath, path);
                }
                catch (IOException) when (File.Exists(path))
                {
                    // another connection may have staged the same content-addressed artifact.
                    VerifyFile(path, bytes.LongLength, expectedSha256);
                }
            }
            finally
            {
                try { File.Delete(temporaryPath); } catch { }
            }
        }

        static void VerifyFile(string path, long expectedLength, string expectedSha256)
        {
            RejectLink(path);
            if (new FileInfo(path).Length != expectedLength)
                throw new InvalidDataException($"Shared artifact '{path}' has an unexpected length.");

            using var stream = File.OpenRead(path);
            using var algorithm = SHA256.Create();
            var actualSha256 = FormatSha256(algorithm.ComputeHash(stream));
            if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Shared artifact '{path}' failed SHA-256 verification.");
        }

        static void RejectLink(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Shared artifact path '{path}' cannot be a symbolic link.");
        }

        internal static string ComputeSha256(byte[] bytes)
        {
            using var algorithm = SHA256.Create();
            return FormatSha256(algorithm.ComputeHash(bytes));
        }

        static string FormatSha256(byte[] hash)
        {
            return string.Create(hash.Length * 2, hash, static (characters, bytes) =>
            {
                const string alphabet = "0123456789abcdef";
                for (var index = 0; index < bytes.Length; index++)
                {
                    characters[index * 2] = alphabet[bytes[index] >> 4];
                    characters[index * 2 + 1] = alphabet[bytes[index] & 0xf];
                }
            });
        }
    }
}
