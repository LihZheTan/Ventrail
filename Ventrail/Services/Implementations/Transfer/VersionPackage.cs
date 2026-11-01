using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LibGit2Sharp;
using Ventrail.Services.Contracts;

namespace Ventrail.Services.Implementations.Transfer;

public record PackageInfo(string FilePath, string Sha256, long Size, string HeadHash, string Branch);

/// <summary>
/// Week 12: a "version package" is what travels between computers: a Git pack file (every saved
/// version, compressed, as Git itself stores them) plus its index, in one file:
///
///   VTPK1\n pack-&lt;sha1&gt;\n &lt;pack length&gt;\n &lt;index length&gt;\n [pack bytes][index bytes]
///
/// The file is then cut into 1 MB blocks (BlockHashing) for streaming. Git objects are named by
/// their own hash, so after the transfer Git can check every version independently.
/// </summary>
public static class VersionPackage
{
    private const string Magic = "VTPK1";
    private static readonly Regex PackName = new("^pack-[0-9a-f]{40}$");

    /// <summary>Packs every version reachable from the current branch of <paramref name="repoPath"/>.</summary>
    public static PackageInfo Create(string repoPath, string outputFolder)
    {
        using var repo = new Repository(repoPath);
        var head = repo.Head.Tip
                   ?? throw new VentrailException("Save a first version before sharing this repository.");
        string branch = repo.Head.FriendlyName;

        Directory.CreateDirectory(outputFolder);
        string work = Path.Combine(outputFolder, "build-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            repo.ObjectDatabase.Pack(new PackBuilderOptions(work), builder =>
            {
                foreach (var commit in repo.Commits.QueryBy(new CommitFilter { IncludeReachableFrom = head }))
                    builder.AddRecursively(commit);   // the version, its folders and its files
            });
            string pack = Directory.GetFiles(work, "pack-*.pack").Single();
            string idx = Path.ChangeExtension(pack, ".idx");
            string name = Path.GetFileNameWithoutExtension(pack);

            string temp = Path.Combine(work, "package.tmp");
            long packLength = new FileInfo(pack).Length, idxLength = new FileInfo(idx).Length;
            using (var output = File.Create(temp))
            {
                output.Write(Encoding.ASCII.GetBytes($"{Magic}\n{name}\n{packLength}\n{idxLength}\n"));
                using (var p = File.OpenRead(pack)) p.CopyTo(output);
                using (var i = File.OpenRead(idx)) i.CopyTo(output);
            }
            string sha = BlockHashing.FileSha256(temp);
            string final = Path.Combine(outputFolder, sha + ".vtpk");
            if (!File.Exists(final)) File.Move(temp, final);
            return new PackageInfo(final, sha, new FileInfo(final).Length, head.Sha, branch);
        }
        finally
        {
            // Our own build folder. Git writes pack files read-only, so clear that first.
            try
            {
                foreach (string f in Directory.GetFiles(work)) File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(work, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Writes the pack and index from a received package into a repository's object folder
    /// (".git/objects/pack"). Refuses anything that isn't a well-formed package.
    /// </summary>
    public static void Unpack(string packageFile, string gitDirectory)
    {
        using var input = File.OpenRead(packageFile);
        string[] header = new string[4];
        for (int i = 0; i < 4; i++) header[i] = ReadLine(input) ?? "";
        if (header[0] != Magic || !PackName.IsMatch(header[1]) ||
            !long.TryParse(header[2], out long packLength) || !long.TryParse(header[3], out long idxLength) ||
            packLength <= 0 || idxLength <= 0 || input.Position + packLength + idxLength != input.Length)
            throw new VentrailException("The received data is not a valid Ventrail version package.");

        string packDir = Path.Combine(gitDirectory, "objects", "pack");
        Directory.CreateDirectory(packDir);
        string name = header[1];
        // The index goes in last: Git only looks at a pack once its index exists.
        Copy(input, Path.Combine(packDir, name + ".pack"), packLength);
        Copy(input, Path.Combine(packDir, name + ".idx"), idxLength);
    }

    private static void Copy(Stream input, string target, long length)
    {
        if (File.Exists(target) && new FileInfo(target).Length == length)
        {
            input.Seek(length, SeekOrigin.Current);   // same name = same content (named by its hash)
            return;
        }
        string temp = target + ".tmp";
        using (var output = File.Create(temp))
        {
            var buffer = new byte[81920];
            long left = length;
            while (left > 0)
            {
                int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                if (read == 0) throw new VentrailException("The received version package is incomplete.");
                output.Write(buffer, 0, read);
                left -= read;
            }
        }
        File.Move(temp, target, overwrite: true);
    }

    private static string? ReadLine(Stream s)
    {
        var bytes = new List<byte>();
        int b;
        while ((b = s.ReadByte()) >= 0 && b != '\n')
        {
            bytes.Add((byte)b);
            if (bytes.Count > 100) return null;
        }
        return b < 0 ? null : Encoding.ASCII.GetString(bytes.ToArray());
    }
}

/// <summary>SHA-256 fingerprints for whole files and for each 1 MB block.</summary>
public static class BlockHashing
{
    public const int BlockSize = 1024 * 1024;   // same as SuggestionWriter.ShareBlockBytes (Week 9)

    public static string Sha256(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string FileSha256(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
    }

    public static List<string> BlockHashes(string path, int blockSize = BlockSize)
    {
        var hashes = new List<string>();
        using var f = File.OpenRead(path);
        var buffer = new byte[blockSize];
        int read;
        while ((read = ReadFull(f, buffer)) > 0) hashes.Add(Sha256(buffer.AsSpan(0, read)));
        return hashes;
    }

    public static int ReadFull(Stream s, byte[] buffer)
    {
        int total = 0, read;
        while (total < buffer.Length && (read = s.Read(buffer, total, buffer.Length - total)) > 0) total += read;
        return total;
    }

    public static bool IsHash(string? s) => s is { Length: 64 } && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
