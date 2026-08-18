using System;
using System.IO;
using OpenCvSharp;

namespace DocConvert.Infrastructure.Windows;

internal static class OpenCvImageFile
{
    internal static Mat Read(string path, ImreadModes mode)
    {
        var image = Cv2.ImDecode(File.ReadAllBytes(path), mode);
        if (image.Empty())
        {
            image.Dispose();
            throw new InvalidOperationException("无法读取图像。");
        }
        return image;
    }

    internal static void Write(string path, Mat image)
    {
        var extension = Path.GetExtension(path);
        if (string.IsNullOrWhiteSpace(extension) || !Cv2.ImEncode(extension, image, out var bytes))
            throw new IOException("无法编码处理后的图像。");
        File.WriteAllBytes(path, bytes);
    }
}
