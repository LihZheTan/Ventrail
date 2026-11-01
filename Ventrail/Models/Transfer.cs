using System;
using System.Collections.Generic;

namespace Ventrail.Models;

/// <summary>Week 12: a repository another computer offers ("LIST" answer).</summary>
public class OfferedRepository
{
    public string PeerId { get; set; } = string.Empty;
    public string PeerName { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;        // DhtKey.ForRepository(first version)
    public string Name { get; set; } = string.Empty;       // folder name, e.g. "Thesis"
    public string HeadHash { get; set; } = string.Empty;   // that computer's latest version
    public string Branch { get; set; } = string.Empty;

    // Set by the Network page: same repository as the one open here, so the button reads
    // "Get latest" instead of "Download a copy".
    public bool IsCurrentRepository { get; set; }

    public string ShortHead => HeadHash.Length >= 7 ? HeadHash[..7] : HeadHash;
    public string ActionText => IsCurrentRepository ? "Get latest" : "Download a copy";
}

/// <summary>
/// Week 12: what the sending computer promises before any data moves: the package's size, its
/// SHA-256 fingerprint and the fingerprint of every 1 MB block. Each block is checked on arrival,
/// then the whole package, then Git checks every version inside it.
/// </summary>
public class TransferManifest
{
    public string PeerId { get; set; } = string.Empty;
    public string RepositoryKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string HeadHash { get; set; } = string.Empty;
    public string Branch { get; set; } = string.Empty;
    public string PackageHash { get; set; } = string.Empty;   // SHA-256 of the whole package (hex)
    public long Size { get; set; }
    public int BlockSize { get; set; }
    public List<string> BlockHashes { get; set; } = new();     // SHA-256 of each block (hex)

    public int BlockCount => BlockHashes.Count;
}

public class TransferProgress
{
    public int BlocksDone { get; init; }
    public int BlockCount { get; init; }
    public long BytesDone { get; init; }
    public long TotalBytes { get; init; }
    public int BlocksResumed { get; init; }   // already on disk from an interrupted download
    public int BlocksRetried { get; init; }   // arrived damaged and were asked for again

    public double Percent => BlockCount == 0 ? 100 : 100.0 * BlocksDone / BlockCount;
}

public enum ImportOutcome
{
    Updated,          // files now match the other computer's latest version
    AlreadyUpToDate,  // same latest version
    AlreadyNewer,     // this computer already has that version and newer ones
    WaitingForSave,   // newer version received, but unsaved changes here: files left untouched
    Diverged,         // both computers saved different versions: kept side by side
    Copied            // a new copy of the repository was created
}

public record ImportResult(ImportOutcome Outcome, string HeadHash, string SavedAs);
