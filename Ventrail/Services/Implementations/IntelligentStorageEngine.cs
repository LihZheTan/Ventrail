using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using Microsoft.ML;
using Microsoft.ML.Trainers.FastTree;
using Ventrail.Models;
using Ventrail.Services.AI;
using Ventrail.Services.Contracts;

namespace Ventrail.Services.Implementations;

/// <summary>
/// Offline ML.NET pre-commit advisor (Fig 3.8).
///
/// Week 8: classifies files as heavy or light with a FastTree model trained offline by
/// Ventrail.ModelTrainer (shipped as MLModels/ventrail_classifier.zip). Everything runs on the
/// user's PC: no file content ever leaves the machine. Only the size and first 4 KB of each
/// file are read, so it is fast even for multi-gigabyte files.
/// Week 9 adds the recommendations (GenerateRecommendation).
/// </summary>
public class IntelligentStorageEngine : IIntelligentStorageEngine
{
    private readonly object _gate = new();
    private PredictionEngine<FileFeatures, HeavyPrediction>? _predictor;   // not thread-safe: always used under _gate
    private bool _loadAttempted;

    public string MlModelPath { get; }

    // Large or specialised formats the advisor should recognise (extend as needed).
    public List<string> SupportedFormats { get; } = new() { ".svs", ".tif", ".tiff", ".psd", ".zip", ".mp4" };

    public string? LoadError { get; private set; }

    public IntelligentStorageEngine(string? modelPath = null)
    {
        MlModelPath = modelPath ?? Path.Combine(AppContext.BaseDirectory, "MLModels", "ventrail_classifier.zip");
    }

    public bool IsModelLoaded
    {
        get
        {
            EnsureLoaded();
            return _predictor != null;
        }
    }

    public string AnalyzeFileFormat(string filePath)
    {
        string ext = Path.GetExtension(filePath);
        string signature;
        try
        {
            signature = File.Exists(filePath) ? FileFeatureExtractor.FromFile(filePath).Signature
                                               : FileFeatureExtractor.Estimate(ext, 0).Signature;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            signature = FileFeatureExtractor.Estimate(ext, 0).Signature;
        }
        return FormatName(signature, ext);
    }

    public string GenerateRecommendation(string fileType, long fileSize) =>
        throw new NotImplementedException("Planned for Week 9: commit message and storage recommendations.");

    public FileClassification? ClassifyFile(string fullPath, long sizeInBytes)
    {
        if (!IsModelLoaded) return null;

        string ext = Path.GetExtension(fullPath);
        FileFeatures features;
        bool estimate = false;
        FileInfo? info = null;
        try
        {
            info = new FileInfo(fullPath);
            if (info.Exists)
            {
                // Same file, same size, same last-change time: reuse the verdict without opening it.
                // (Every tick of a checkbox rescans the folder, and opening each file again is slow
                // because Windows' antivirus checks it.)
                if (_cache.TryGetValue(fullPath, out var hit) &&
                    hit.Size == info.Length && hit.Modified == info.LastWriteTimeUtc)
                    return hit.Result;

                features = FileFeatureExtractor.FromFile(fullPath);
                sizeInBytes = info.Length;
            }
            else
            {
                features = FileFeatureExtractor.Estimate(ext, sizeInBytes);
                estimate = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked by another program: fall back to an estimate from the extension and size.
            features = FileFeatureExtractor.Estimate(ext, sizeInBytes);
            estimate = true;
        }

        HeavyPrediction prediction;
        lock (_gate) prediction = _predictor!.Predict(features);

        var result = new FileClassification
        {
            IsHeavy = prediction.IsHeavy,
            Probability = prediction.Probability,
            FormatName = FormatName(features.Signature, ext),
            IsAlreadyCompressed = IsCompressedFormat(features.Signature),
            IsEstimate = estimate,
            SizeInBytes = sizeInBytes
        };
        if (!estimate && info != null)
            _cache[fullPath] = (info.Length, info.LastWriteTimeUtc, result);
        return result;
    }

    private readonly ConcurrentDictionary<string, (long Size, DateTime Modified, FileClassification Result)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    // Loads the model once, on first use (always called from a background thread by the ViewModel).
    private void EnsureLoaded()
    {
        lock (_gate)
        {
            if (_loadAttempted) return;
            _loadAttempted = true;
            try
            {
                if (!File.Exists(MlModelPath))
                {
                    LoadError = $"Model file not found: {MlModelPath}";
                    return;
                }
                var ml = new MLContext(seed: 42);
                // Make sure ML.NET can find the FastTree components the saved model refers to.
                ml.ComponentCatalog.RegisterAssembly(typeof(FastTreeBinaryTrainer).Assembly);
                ITransformer model = ml.Model.Load(MlModelPath, out _);
                _predictor = ml.Model.CreatePredictionEngine<FileFeatures, HeavyPrediction>(model);
            }
            catch (Exception ex)
            {
                // A broken model must never stop the app: the advisor is simply switched off.
                LoadError = ex.Message;
                _predictor = null;
            }
        }
    }

    private static bool IsCompressedFormat(string signature) => signature is
        "zip" or "png" or "jpeg" or "gif" or "mp4" or "mkv" or "gzip" or "7z" or "rar" or "mp3" or "flac" or "pdf";

    // Friendly names for the UI. The extension refines the header (a "zip" may be a Word file).
    private static string FormatName(string signature, string extension) => extension.ToLowerInvariant() switch
    {
        ".docx" or ".doc" => "Word document",
        ".xlsx" or ".xls" => "Excel spreadsheet",
        ".pptx" or ".ppt" => "PowerPoint presentation",
        ".svs" => "whole-slide image",
        ".psd" => "Photoshop file",
        _ => signature switch
        {
            "zip" => "ZIP archive",
            "png" => "PNG picture",
            "jpeg" => "JPEG picture",
            "gif" => "GIF picture",
            "pdf" => "PDF document",
            "tiff" => "TIFF image",
            "psd" => "Photoshop file",
            "ole" => "Office document",
            "exe" => "program file",
            "gzip" or "7z" or "rar" => "compressed archive",
            "riff" => "audio/video file",
            "mp3" or "flac" => "audio file",
            "mp4" or "mkv" => "video",
            "font" => "font",
            "text" => "text file",
            _ => "file"
        }
    };
}
