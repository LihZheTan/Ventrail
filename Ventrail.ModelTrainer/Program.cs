using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Microsoft.ML;
using Microsoft.ML.Data;
using Ventrail.Services.AI;

// =====================================================================================
// Ventrail model trainer (CP2 Week 8)
//
//   dotnet run --project Ventrail.ModelTrainer                    (system folders only)
//   dotnet run --project Ventrail.ModelTrainer -- --include "D:\MyFiles" --max-files 8000
//
// 1. Collect sample files. By default only non-personal places are read (Windows media and
//    wallpapers, Program Files, the NuGet cache); extra folders only with --include.
// 2. Label each file by MEASURING its real storage cost: the size after deflate (the zlib
//    compression Git uses for every stored object). Heavy = one version adds >= 1 MB.
// 3. Add synthetic large text files (logs, CSV exports) that are rare in system folders.
// 4. Train an ML.NET FastTree binary classifier on the CHEAP features only (size + first
//    4 KB), so the app can predict the expensive measurement without compressing the file.
// 5. Evaluate on a held-out 20% and compare with the simple size-only rules.
// 6. Save the model to ../Ventrail/MLModels/ventrail_classifier.zip (shipped with the app).
// =====================================================================================

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var includes = new List<string>();
int maxFiles = 6000;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--include" && i + 1 < args.Length) includes.Add(args[++i]);
    else if (args[i] == "--max-files" && i + 1 < args.Length) maxFiles = int.Parse(args[++i]);
}

string trainerDir = FindTrainerDir();
string dataDir = Path.Combine(trainerDir, "data");
string modelPath = Path.GetFullPath(Path.Combine(trainerDir, "..", "Ventrail", "MLModels", "ventrail_classifier.zip"));
Directory.CreateDirectory(dataDir);
Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);

var clock = Stopwatch.StartNew();

// ---------- 1. collect files ----------
string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
var sources = new List<(string Path, string Kind)>
{
    (Path.Combine(win, "Web"), "system"),
    (Path.Combine(win, "Media"), "system"),
    (Path.Combine(win, "Fonts"), "system"),
    (Path.Combine(win, "Help"), "system"),
    (Path.Combine(win, "Logs"), "system"),
    (pf, "system"),
    (pf86, "system"),
    (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages"), "system"),
    (@"C:\Users\Public", "system"),
};
sources.AddRange(includes.Select(p => (p, "user")));

var rng = new Random(42);
var candidates = new List<(string Path, string Kind)>();
foreach (var (root, kind) in sources.Where(s => Directory.Exists(s.Path)))
{
    var opts = new EnumerationOptions
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Offline
    };
    int n = 0;
    foreach (string f in Directory.EnumerateFiles(root, "*", opts))
    {
        candidates.Add((f, kind));
        if (++n >= 300_000) break;
    }
    Console.WriteLine($"  {root}: {n} files found");
}

// Shuffle, then cap each extension so DLLs don't drown out everything else.
var perExt = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
const int maxPerExtension = 250;
var picked = new List<(string Path, string Kind)>();
foreach (var c in candidates.OrderBy(_ => rng.Next()))
{
    if (picked.Count >= maxFiles) break;
    string ext = Path.GetExtension(c.Path);
    perExt.TryGetValue(ext, out int used);
    if (used >= maxPerExtension) continue;
    perExt[ext] = used + 1;
    picked.Add(c);
}
Console.WriteLine($"Picked {picked.Count} files ({perExt.Count} extensions). Measuring storage cost...");

// ---------- 2. features + measured label ----------
var rows = new List<Row>();
foreach (var (path, kind) in picked)
{
    try
    {
        var info = new FileInfo(path);
        if (info.Length == 0) continue;
        FileFeatures features = FileFeatureExtractor.FromFile(path);
        long stored = MeasureStoredBytes(path, info.Length);
        rows.Add(new Row(features, info.Extension.ToLowerInvariant(), info.Length, stored, kind));
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* locked or protected: skip */ }
}

// ---------- 3. synthetic large text files ----------
foreach (var row in Synthetic(rng, 300)) rows.Add(row);

int heavy = rows.Count(r => r.Features.IsHeavy);
Console.WriteLine($"Dataset: {rows.Count} rows, {heavy} heavy ({100.0 * heavy / rows.Count:F1}%), {clock.Elapsed.TotalSeconds:F0}s");

string csvPath = Path.Combine(dataDir, "training-data.csv");
WriteCsv(csvPath, rows);

// ---------- 4. train ----------
var ml = new MLContext(seed: 42);
IDataView all = ml.Data.LoadFromTextFile<FileFeatures>(csvPath, hasHeader: true, separatorChar: ',');
var split = ml.Data.TrainTestSplit(all, testFraction: 0.2, seed: 42);

var pipeline = ml.Transforms.Categorical.OneHotEncoding("SignatureEncoded", nameof(FileFeatures.Signature))
    .Append(ml.Transforms.Categorical.OneHotEncoding("CategoryEncoded", nameof(FileFeatures.Category)))
    .Append(ml.Transforms.Concatenate("Features",
        nameof(FileFeatures.LogSize), nameof(FileFeatures.HeaderEntropy),
        nameof(FileFeatures.ZeroByteRatio), nameof(FileFeatures.PrintableRatio),
        "SignatureEncoded", "CategoryEncoded"))
    .Append(ml.BinaryClassification.Trainers.FastTree(
        numberOfLeaves: 16, numberOfTrees: 100, minimumExampleCountPerLeaf: 10, learningRate: 0.2));

ITransformer model = pipeline.Fit(split.TrainSet);

// ---------- 5. evaluate ----------
var metrics = ml.BinaryClassification.Evaluate(model.Transform(split.TestSet));
var cv = ml.BinaryClassification.CrossValidate(all, pipeline, numberOfFolds: 5);
double cvAccuracy = cv.Average(f => f.Metrics.Accuracy);
double cvAccuracySd = Math.Sqrt(cv.Average(f => Math.Pow(f.Metrics.Accuracy - cvAccuracy, 2)));

// Baselines on the same test rows: the size-only rules a non-ML version would use.
var testRows = ml.Data.CreateEnumerable<FileFeatures>(split.TestSet, reuseRowObject: false).ToList();
// Only ~4% of files are heavy, so accuracy alone flatters any rule that says "light" a lot:
// precision, recall and F1 for the heavy class are the fairer comparison.
(double Acc, double P, double R, double F1) SizeRule(double minBytes)
{
    int tp = 0, fp = 0, fn = 0, ok = 0;
    foreach (var r in testRows)
    {
        bool predicted = Math.Pow(10, r.LogSize) - 1 >= minBytes;
        if (predicted == r.IsHeavy) ok++;
        if (predicted && r.IsHeavy) tp++;
        else if (predicted) fp++;
        else if (r.IsHeavy) fn++;
    }
    double p = tp + fp == 0 ? 0 : (double)tp / (tp + fp);
    double rc = tp + fn == 0 ? 0 : (double)tp / (tp + fn);
    return ((double)ok / testRows.Count, p, rc, p + rc == 0 ? 0 : 2 * p * rc / (p + rc));
}
var rule50Mb = SizeRule(50.0 * 1024 * 1024);   // Week 6 "Large" badge
var rule1Mb = SizeRule(1024 * 1024);           // "big file = heavy"
string RuleRow(string name, (double Acc, double P, double R, double F1) m) =>
    $"| {name} | {m.Acc:P2} | {m.P:F3} | {m.R:F3} | {m.F1:F3} |";

var cm = metrics.ConfusionMatrix;
var report = new StringBuilder()
    .AppendLine("# Ventrail heavy/light classifier - training report")
    .AppendLine()
    .AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm}  ")
    .AppendLine($"Trainer: ML.NET FastTree (16 leaves, 100 trees), seed 42  ")
    .AppendLine($"Heavy = one saved version adds >= {FileFeatureExtractor.HeavyThresholdBytes / 1024 / 1024} MB after deflate (Git's zlib)  ")
    .AppendLine()
    .AppendLine("## Dataset")
    .AppendLine($"- Rows: {rows.Count} ({rows.Count(r => r.Source == "system")} system files, " +
                $"{rows.Count(r => r.Source == "user")} user-included files, {rows.Count(r => r.Source == "synthetic")} synthetic)")
    .AppendLine($"- Heavy: {heavy} ({100.0 * heavy / rows.Count:F1}%), light: {rows.Count - heavy}")
    .AppendLine($"- Extensions: {rows.Select(r => r.Extension).Distinct().Count()}")
    .AppendLine($"- Train/test split: 80/20")
    .AppendLine()
    .AppendLine("## Results on the held-out test set")
    .AppendLine("| Metric | Value |")
    .AppendLine("|---|---|")
    .AppendLine($"| Accuracy | {metrics.Accuracy:P2} |")
    .AppendLine($"| AUC | {metrics.AreaUnderRocCurve:F4} |")
    .AppendLine($"| F1 (heavy) | {metrics.F1Score:F4} |")
    .AppendLine($"| Precision (heavy) | {metrics.PositivePrecision:F4} |")
    .AppendLine($"| Recall (heavy) | {metrics.PositiveRecall:F4} |")
    .AppendLine($"| 5-fold cross-validation accuracy | {cvAccuracy:P2} (+/- {cvAccuracySd:P2}) |")
    .AppendLine()
    .AppendLine("Confusion matrix (test set; positive = heavy):")
    .AppendLine()
    .AppendLine("```")
    .AppendLine(cm.GetFormattedConfusionTable().Trim())
    .AppendLine("```")
    .AppendLine()
    .AppendLine("## Baselines (same test set)")
    .AppendLine("| Rule | Accuracy | Precision (heavy) | Recall (heavy) | F1 (heavy) |")
    .AppendLine("|---|---|---|---|---|")
    .AppendLine(RuleRow("Size >= 50 MB (Week 6 'Large' badge)", rule50Mb))
    .AppendLine(RuleRow("Size >= 1 MB", rule1Mb))
    .AppendLine(RuleRow("ML.NET model", (metrics.Accuracy, metrics.PositivePrecision, metrics.PositiveRecall, metrics.F1Score)))
    .AppendLine()
    .AppendLine($"Total time: {clock.Elapsed.TotalSeconds:F0}s");

File.WriteAllText(Path.Combine(dataDir, "metrics.md"), report.ToString());
Console.WriteLine();
Console.WriteLine(report);

// ---------- 6. save ----------
ml.Model.Save(model, all.Schema, modelPath);
Console.WriteLine($"Model saved: {modelPath} ({new FileInfo(modelPath).Length / 1024} KB)");

// =====================================================================================

// Storage cost of one version = deflated size. Files over 16 MB: compress 8 evenly spread
// 1 MB samples and scale up, which keeps training fast with almost the same answer.
static long MeasureStoredBytes(string path, long size)
{
    const int chunk = 1024 * 1024;
    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    if (size <= 16L * chunk)
        return DeflatedLength(fs);

    long sampled = 0, compressed = 0;
    var buffer = new byte[chunk];
    for (int i = 0; i < 8; i++)
    {
        fs.Position = (size - chunk) * i / 7;
        int n = fs.ReadAtLeast(buffer, chunk, throwOnEndOfStream: false);
        sampled += n;
        compressed += DeflatedLength(new MemoryStream(buffer, 0, n));
    }
    return (long)((double)compressed / sampled * size);
}

static long DeflatedLength(Stream input)
{
    var counter = new CountingStream();
    using (var deflate = new ZLibStream(counter, CompressionLevel.Optimal, leaveOpen: true))
        input.CopyTo(deflate);
    return counter.Count;
}

// Large text files (logs, CSV exports, plain-text notes) are rare in system folders but common
// for students and researchers, so add some: they are big on disk but compress well.
static IEnumerable<Row> Synthetic(Random rng, int count)
{
    string[] words = "the data sample result value method analysis figure table chapter study participant group mean error".Split(' ');
    for (int i = 0; i < count; i++)
    {
        long size = (long)Math.Pow(10, 5 + rng.NextDouble() * 2.6);   // 100 KB - 40 MB, log-uniform
        int kind = i % 3;
        string ext = kind switch { 0 => ".log", 1 => ".csv", _ => ".txt" };
        var sb = new StringBuilder();
        while (sb.Length < size)
        {
            switch (kind)
            {
                case 0:
                    sb.Append($"2026-10-{rng.Next(1, 29):00} {rng.Next(24):00}:{rng.Next(60):00}:{rng.Next(60):00} ")
                      .Append(rng.Next(4) == 0 ? "WARN " : "INFO ")
                      .Append($"request {rng.Next(100000)} took {rng.Next(2000)} ms user={rng.Next(500)}\r\n");
                    break;
                case 1:
                    sb.Append($"{rng.Next(100000)},{rng.NextDouble() * 100:F4},{rng.NextDouble() * 1000:F2},{words[rng.Next(words.Length)]},{rng.Next(2)}\r\n");
                    break;
                default:
                    for (int w = 0; w < 12; w++) sb.Append(words[rng.Next(words.Length)]).Append(' ');
                    sb.Append(".\r\n");
                    break;
            }
        }
        byte[] bytes = Encoding.UTF8.GetBytes(sb.ToString(0, (int)size));
        var features = FileFeatureExtractor.FromHeader(ext, bytes.Length,
            bytes.AsSpan(0, Math.Min(FileFeatureExtractor.HeaderBytes, bytes.Length)));
        long stored = DeflatedLength(new MemoryStream(bytes));
        yield return new Row(features, ext, bytes.Length, stored, "synthetic");
    }
}

static void WriteCsv(string path, List<Row> rows)
{
    // No file names or paths are stored: only the features, the label and a few numbers for analysis.
    using var w = new StreamWriter(path, false, new UTF8Encoding(false));
    w.WriteLine("LogSize,HeaderEntropy,ZeroByteRatio,PrintableRatio,Signature,Category,IsHeavy,Extension,SizeBytes,StoredBytes,Source");
    foreach (var r in rows)
    {
        var f = r.Features;
        w.WriteLine(string.Join(',', f.LogSize.ToString("F4"), f.HeaderEntropy.ToString("F4"),
            f.ZeroByteRatio.ToString("F4"), f.PrintableRatio.ToString("F4"), f.Signature, f.Category,
            f.IsHeavy ? "true" : "false", r.Extension.Replace(",", ""), r.Size, r.Stored, r.Source));
    }
}

static string FindTrainerDir()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Ventrail.ModelTrainer.csproj")))
        dir = dir.Parent;
    return dir?.FullName ?? Directory.GetCurrentDirectory();
}

record Row(FileFeatures Features, string Extension, long Size, long Stored, string Source)
{
    // The label: measured, not guessed.
    public FileFeatures Features { get; } = SetLabel(Features, Stored);
    static FileFeatures SetLabel(FileFeatures f, long stored) { f.IsHeavy = stored >= FileFeatureExtractor.HeavyThresholdBytes; return f; }
}

// Counts bytes written without keeping them (we only need the compressed length).
class CountingStream : Stream
{
    public long Count { get; private set; }
    public override void Write(byte[] buffer, int offset, int count) => Count += count;
    public override void Write(ReadOnlySpan<byte> buffer) => Count += buffer.Length;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => Count;
    public override long Position { get => Count; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
