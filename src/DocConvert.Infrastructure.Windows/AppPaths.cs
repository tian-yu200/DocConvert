using System;
using System.IO;
using System.Linq;

namespace DocConvert.Infrastructure.Windows;

public static class AppPaths
{
    public static string FindTessdata()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "tessdata"),
            Path.Combine(AppContext.BaseDirectory, "assets", "tessdata")
        };
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            candidates.Add(Path.Combine(directory.FullName, "assets", "tessdata"));
        return candidates.FirstOrDefault(Directory.Exists) ?? candidates[0];
    }

    public static void RequireOcrLanguages(string tessdata, string languages)
    {
        var missing = languages.Split('+', StringSplitOptions.RemoveEmptyEntries)
            .Where(language => !File.Exists(Path.Combine(tessdata, language + ".traineddata")))
            .ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException($"缺少 OCR 模型：{string.Join(", ", missing)}。请确认 tessdata 已随应用安装。");
    }
}
