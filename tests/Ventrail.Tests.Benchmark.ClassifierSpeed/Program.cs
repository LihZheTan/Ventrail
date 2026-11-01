using System.Diagnostics;
using System.IO;
using Ventrail.Services.AI;
using Ventrail.Services.Implementations;

// Week 8 speed check of the ML.NET classifier (timings only, no PASS/FAIL).
var engine = new IntelligentStorageEngine(TestPaths.Model);
var sw = Stopwatch.StartNew();
bool ok = engine.IsModelLoaded;
Console.WriteLine($"load {sw.ElapsedMilliseconds} ms ({ok})");

// existing (already-scanned) files: the project's own source files
var files = Directory.GetFiles(TestPaths.Project, "*.cs", SearchOption.AllDirectories).Take(200).ToList();
sw.Restart(); foreach (var f in files) FileFeatureExtractor.FromFile(f);
Console.WriteLine($"features only, {files.Count} existing files: {sw.ElapsedMilliseconds} ms");
sw.Restart(); foreach (var f in files) engine.ClassifyFile(f, 0);
Console.WriteLine($"classify (features+predict), {files.Count} existing files: {sw.ElapsedMilliseconds} ms");
var est = Enumerable.Range(0, 500).Select(i => $@"C:\nope\f{i}.txt").ToList();
sw.Restart(); foreach (var f in est) engine.ClassifyFile(f, 5000);
Console.WriteLine($"predict only (no file IO), 500: {sw.ElapsedMilliseconds} ms");
