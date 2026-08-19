using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig;

namespace DocConvert.Infrastructure.Windows;

public static class DocumentSafety
{
    public static void EnsureModifiable(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("找不到输入文件。", path);
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly))
            throw new InvalidOperationException("文件为只读状态，已拒绝修改。请先确认您有权修改该文件。");

        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".docx" or ".xlsx" or ".pptx") EnsureOpenXmlModifiable(path);
        if (extension == ".pdf") EnsurePdfModifiable(path);
    }

    private static void EnsureOpenXmlModifiable(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            if (archive.Entries.Any(entry => entry.FullName.StartsWith("_xmlsignatures/", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("文件带有数字签名，已拒绝处理。应用不会移除签名。");
            if (archive.Entries.Any(entry => entry.FullName.Equals("EncryptionInfo", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("文件已加密或受权限保护，已拒绝处理。");
        }
        catch (InvalidDataException)
        {
            throw new InvalidOperationException("文件已损坏、加密或不是有效的 Open XML 文档。");
        }
    }

    private static void EnsurePdfModifiable(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var marker = System.Text.Encoding.ASCII.GetString(bytes);
        if (marker.Contains("/ByteRange", StringComparison.Ordinal) || marker.Contains("/Sig", StringComparison.Ordinal))
            throw new InvalidOperationException("PDF 带有数字签名，已拒绝处理。应用不会移除签名。");
        try
        {
            using var structureDocument = PdfLexer.PdfDocument.Open(path);
            using (var securityDocument = PdfReader.Open(path, PdfDocumentOpenMode.Import))
            {
                if (securityDocument.SecuritySettings.IsEncrypted)
                    throw new InvalidOperationException("PDF 已加密或受权限保护，已拒绝处理。应用不会移除密码或权限设置。");
            }
            using var document = PdfDocument.Open(path);
            _ = document.NumberOfPages;
        }
        catch (PdfLexer.PdfLexerPasswordException exception)
        {
            throw new InvalidOperationException("PDF 已加密或受权限保护，已拒绝处理。应用不会移除密码或权限设置。", exception);
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains("应用不会移除密码或权限设置", StringComparison.Ordinal))
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("PDF 已加密、受权限限制或无法读取，已拒绝处理。", exception);
        }
    }
}
