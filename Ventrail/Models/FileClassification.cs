using System;

namespace Ventrail.Models;

/// <summary>
/// Week 8: the storage advisor's verdict for one file. "Heavy" means saving one new version
/// adds at least 1 MB to the history after Git's compression (see Ventrail.ModelTrainer).
/// </summary>
public class FileClassification
{
    public bool IsHeavy { get; set; }
    public float Probability { get; set; }            // model's probability that the file is heavy
    public string FormatName { get; set; } = "file";  // e.g. "JPEG picture", from the header bytes
    public bool IsAlreadyCompressed { get; set; }     // zip/docx/jpeg/mp4...: Git can't shrink these
    public bool IsEstimate { get; set; }              // file not on disk: guessed from the extension
    public long SizeInBytes { get; set; }

    // How sure the model is about the verdict it gave (not just about "heavy").
    // Capped at 99%: tree models often output 0 or 1, but "100% sure" would overstate it.
    public float Confidence => Math.Min(IsHeavy ? Probability : 1 - Probability, 0.99f);

    public string Label => IsHeavy ? "Heavy" : "Light";

    public string Summary
    {
        get
        {
            string size = FileAsset.FormatSize(SizeInBytes);
            string text = IsHeavy
                ? IsAlreadyCompressed
                    ? $"Heavy file ({Confidence:P0} sure). This {FormatName} is already compressed, so every saved " +
                      $"version stores a full new copy in the history (about {size} each time)."
                    : $"Heavy file ({Confidence:P0} sure). Even after compression, every saved version of this " +
                      $"{FormatName} adds more than 1 MB to the history."
                : $"Light file ({Confidence:P0} sure). Saving new versions of this {FormatName} adds little to the " +
                  "history (under 1 MB each).";
            return IsEstimate ? text + " Estimated from the file type, because the file isn't on disk." : text;
        }
    }
}
