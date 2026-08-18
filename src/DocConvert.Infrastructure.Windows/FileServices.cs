using System;
using System.Collections.Generic;
using System.IO;
using DocConvert.Core;

namespace DocConvert.Infrastructure.Windows;

public sealed class OutputPathService : IOutputPathService
{
    public string GetUniquePath(string desiredPath)
    {
        if (!File.Exists(desiredPath)) return desiredPath;

        var directory = Path.GetDirectoryName(desiredPath)!;
        var name = Path.GetFileNameWithoutExtension(desiredPath);
        var extension = Path.GetExtension(desiredPath);
        for (var index = 1; index < 10_000; index++)
        {
            var candidate = Path.Combine(directory, $"{name} ({index}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }

        throw new IOException("无法为输出文件分配唯一名称。");
    }

    public string CreateWatermarkOutputPath(string inputPath)
    {
        var directory = Path.GetDirectoryName(inputPath)!;
        var desired = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(inputPath)}_无水印{Path.GetExtension(inputPath)}");
        return GetUniquePath(desired);
    }

    public string CreateConvertedOutputPath(string inputPath, string extension)
    {
        extension = extension.StartsWith('.') ? extension : $".{extension}";
        var directory = Path.GetDirectoryName(inputPath)!;
        return GetUniquePath(Path.Combine(directory, Path.GetFileNameWithoutExtension(inputPath) + extension));
    }
}

public sealed class JobWorkspace : IDisposable
{
    public JobWorkspace(Guid jobId)
    {
        Root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DocConvert", "Temp", jobId.ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }
    public string PathFor(string fileName) => Path.Combine(Root, fileName);

    public string Commit(string temporaryPath, string finalPath)
    {
        if (!File.Exists(temporaryPath) || new FileInfo(temporaryPath).Length == 0)
            throw new IOException("转换引擎没有生成有效的临时输出文件。");
        if (Path.GetFullPath(temporaryPath).Equals(Path.GetFullPath(finalPath), StringComparison.OrdinalIgnoreCase))
            throw new IOException("输出路径不能与临时文件相同。");
        if (File.Exists(finalPath))
            throw new IOException("输出文件已存在。为保护现有文件，本次任务未覆盖它。");
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        File.Move(temporaryPath, finalPath, false);
        return finalPath;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
        catch { }
    }

    public static void CleanupOld()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DocConvert", "Temp");
        if (!Directory.Exists(root)) return;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow.AddHours(-24))
                    Directory.Delete(directory, true);
            }
            catch { }
        }
    }
}

public static class SupportedFiles
{
    public static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
    { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff" };

    public static readonly HashSet<string> Office = new(StringComparer.OrdinalIgnoreCase)
    { ".docx", ".xlsx", ".pptx" };

    public static bool IsImage(string path) => Images.Contains(Path.GetExtension(path));
}
