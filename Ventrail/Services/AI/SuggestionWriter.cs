using System;
using System.Collections.Generic;
using System.Linq;
using Ventrail.Models;

namespace Ventrail.Services.AI;

/// <summary>
/// Week 9: turns the classifier's verdicts into advice a non-technical user can act on:
/// a suggested commit message, storage tips for heavy files, and how a file is split into blocks
/// when it is shared with peers (Week 12). Rule-based and offline: the same files always get the
/// same advice, and nothing leaves the PC.
/// </summary>
public static class SuggestionWriter
{
    // Peer-to-peer transfers (Week 12) send files in blocks of this size, each checked by its hash.
    public const int ShareBlockBytes = 1024 * 1024;

    // Git tools cut a message's first line after about 72 characters.
    public const int MaxMessageLength = 72;

    public static long BlockCount(long sizeInBytes) =>
        Math.Max(1, (sizeInBytes + ShareBlockBytes - 1) / ShareBlockBytes);

    public static CommitSuggestion Suggest(IReadOnlyList<FileAsset> staged) => new()
    {
        Message = WriteMessage(staged),
        FileCount = staged.Count,
        Advice = staged.Where(f => f.TrackStatus != "Deleted")
                       .Select(Advise).OfType<StorageAdvice>()
                       .OrderByDescending(a => a.Level).ThenByDescending(a => a.SizeInBytes)
                       .ToList()
    };

    // ---------- commit message ----------

    // Status -> verb, in the order the clauses appear ("Update ..., add ... and remove ...").
    private static readonly (string Status, string Verb)[] Verbs =
        { ("Modified", "Update"), ("Added", "Add"), ("Deleted", "Remove"), ("Renamed", "Rename") };

    /// <summary>
    /// e.g. "Update Chapter3_Methodology.docx", "Add 3 pictures in figures",
    /// "Update 3 files, add 2 pictures and remove old_ideas.txt". Tries the most detailed wording
    /// first and falls back to counts when it would be longer than 72 characters.
    /// </summary>
    public static string WriteMessage(IReadOnlyList<FileAsset> files)
    {
        if (files.Count == 0) return string.Empty;

        var groups = Verbs
            .Select(v => (v.Verb, Files: files.Where(f => f.TrackStatus == v.Status).ToList()))
            .ToList();
        // Any other status (e.g. a staged "Untracked" file) counts as an update.
        groups[0].Files.AddRange(files.Where(f => Verbs.All(v => v.Status != f.TrackStatus)));
        groups.RemoveAll(g => g.Files.Count == 0);

        string message = Enumerable.Range(0, 3)
            .Select(detail => JoinClauses(groups.Select(g => Clause(g.Verb, g.Files, detail)).ToList()))
            .FirstOrDefault(m => m.Length <= MaxMessageLength)
            ?? $"Save changes to {files.Count} files";

        string? folder = CommonFolder(files);
        if (folder != null && files.Count > 1 && message.Length + folder.Length + 4 <= MaxMessageLength)
            message += $" in {folder}";
        return message;
    }

    // detail 0: name files where it reads well; 1: name single files only; 2: counts only.
    private static string Clause(string verb, List<FileAsset> files, int detail)
    {
        var kinds = files.Select(f => FileFeatureExtractor.CategoryOf(System.IO.Path.GetExtension(f.FileName)))
                         .Distinct().ToList();
        string noun = kinds.Count == 1 ? Plural(kinds[0]) : "files";

        if (files.Count == 1)
            return detail < 2 ? $"{verb} {files[0].FileName}" : $"{verb} 1 {noun[..^1]}";
        if (detail == 0 && kinds.Count > 1)
            return files.Count == 2
                ? $"{verb} {files[0].FileName} and {files[1].FileName}"
                : $"{verb} {files.MaxBy(f => f.SizeInBytes)!.FileName} and {files.Count - 1} other files";
        return $"{verb} {files.Count} {noun}";
    }

    private static string JoinClauses(List<string> clauses)
    {
        // Only the first clause starts with a capital: "Update ..., add ... and remove ...".
        var parts = clauses.Select((c, i) => i == 0 ? c : char.ToLowerInvariant(c[0]) + c[1..]).ToList();
        return parts.Count == 1 ? parts[0]
            : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];
    }

    private static string Plural(string category) => category switch
    {
        "document" => "documents",
        "data" => "data files",
        "text" => "text files",
        "code" => "code files",
        "image" => "pictures",
        "video" => "videos",
        "audio" => "audio files",
        "archive" => "archives",
        "executable" => "programs",
        "font" => "fonts",
        _ => "files"
    };

    // The top-level folder all files are in, e.g. "figures" (null if they are spread out).
    private static string? CommonFolder(IReadOnlyList<FileAsset> files)
    {
        var tops = files.Select(f =>
        {
            int slash = f.FilePath.IndexOfAny(new[] { '/', '\\' });
            return slash > 0 ? f.FilePath[..slash] : null;
        }).Distinct().ToList();
        return tops.Count == 1 ? tops[0] : null;
    }

    // ---------- storage advice ----------

    // One line for the suggestion card, or null if the file needs no advice.
    private static StorageAdvice? Advise(FileAsset file)
    {
        var verdict = file.Classification;
        bool needsAdvice = verdict?.IsHeavy ?? file.IsLarge;   // no model: fall back to the Week 6 size rule
        if (!needsAdvice) return null;

        bool veryLarge = file.SizeInBytes >= FileAsset.LargeFileBytes;
        string size = FileAsset.FormatSize(file.SizeInBytes);
        string text = veryLarge
            ? $"{file.FileName} ({size}) is very large. Every version keeps another full copy, so consider leaving it out."
            : verdict?.IsAlreadyCompressed == true
                ? $"{file.FileName} ({size}) is already compressed, so each version stores a full copy."
                : $"{file.FileName} ({size}) adds over 1 MB to the history each time it is saved.";

        return new StorageAdvice
        {
            FilePath = file.FilePath,
            FileName = file.FileName,
            SizeInBytes = file.SizeInBytes,
            Text = text,
            Level = veryLarge ? AdviceLevel.Warning : AdviceLevel.Info,
            SuggestLeaveOut = veryLarge
        };
    }

    /// <summary>
    /// Longer tip for the File Inspector: when to save the file, and how it is sent in blocks
    /// when the folder is shared. Empty when there is nothing useful to say.
    /// </summary>
    public static string StorageTip(FileClassification? verdict, long sizeInBytes)
    {
        bool veryLarge = sizeInBytes >= FileAsset.LargeFileBytes;
        if (verdict is null)
            return veryLarge ? "Tip: every saved version keeps a full copy of this file, so save it only when it matters." : string.Empty;
        if (!verdict.IsHeavy)
            return "Tip: save new versions as often as you like. Each one takes very little space.";

        string tip = verdict.IsAlreadyCompressed
            ? "Tip: save this file at milestones (a finished draft, a final figure) rather than after every small edit."
            : $"Tip: Git shrinks this {verdict.FormatName} a little, but it is still best saved at milestones " +
              "rather than after every small edit.";
        if (veryLarge)
            tip += " It is very large, so ask whether it needs a history at all: you can untick it and keep it as a normal file.";

        long blocks = BlockCount(sizeInBytes);
        tip += $" When you share this folder, it is sent in {blocks:N0} block{(blocks == 1 ? "" : "s")} of 1 MB, " +
               "each checked against its fingerprint (hash), so an interrupted transfer carries on where it stopped.";
        return tip;
    }
}
