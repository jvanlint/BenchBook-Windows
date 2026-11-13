using System.Security.Cryptography;
using System.Text;
using BenchBook.Core.Model;

namespace BenchBook.Core.Capture;

/// <summary>A file the user is adding, as its contents showed it to be.</summary>
public sealed record InspectedFile(
    string Kind, string MimeType, string Extension, long ByteCount, string Sha256, int? PixelWidth, int? PixelHeight);

/// <summary>A capture that can't be saved. The message is for the user.</summary>
public sealed class CaptureException(string message) : Exception(message)
{
    public static CaptureException Unsupported(string name) =>
        new($"“{name}” isn't a supported file. BenchBook accepts JPEG, PNG, WebP and HEIC images, PDFs and Markdown files.");

    public static CaptureException Unreadable(string name) =>
        new($"“{name}” couldn't be read. It may be damaged or empty.");

    public static CaptureException TooLarge(string name) =>
        new($"“{name}” is too large. Files can be up to 200 MB, and Markdown files up to 5 MB.");

    public static CaptureException TooManyPixels(string name) =>
        new($"“{name}” has too many pixels. Images can be up to 300 megapixels.");

    public static CaptureException InvalidLink(string link) =>
        new($"“{link}” isn't a web link BenchBook can save.");

    public static CaptureException EmptyText() => new("The shared text was empty.");

    public static CaptureException ProjectNotFound() => new("The project these items were sent to no longer exists.");

    public static CaptureException EmptyNote() => new("Write a title or some text for the note.");
}

/// <summary>
/// Checks a file by its contents, not its name, except Markdown, which has no
/// signature (behaviour.md, "Files references accept").
/// </summary>
public static class FileInspector
{
    public const long MaxFileBytes = 200L * 1024 * 1024;
    public const long MaxMarkdownBytes = 5L * 1024 * 1024;
    public const long MaxImagePixels = 300_000_000;

    private static readonly HashSet<string> MarkdownExtensions = new(StringComparer.OrdinalIgnoreCase) { ".md", ".markdown", ".mdown", ".mkd" };

    /// <param name="path">The file to check.</param>
    /// <param name="displayName">The name to use in messages.</param>
    /// <param name="pdfHasPages">Opens a PDF and says whether it has at least one page.</param>
    public static InspectedFile Inspect(string path, string displayName, Func<string, bool> pdfHasPages)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists) throw CaptureException.Unreadable(displayName);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw CaptureException.Unreadable(displayName);
        }
        if (info.Length == 0) throw CaptureException.Unreadable(displayName);
        if (info.Length > MaxFileBytes) throw CaptureException.TooLarge(displayName);

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var header = new byte[1024];
            var read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            var start = header.AsSpan(0, read);

            InspectedFile result;
            if (ImageHeaders.Recognise(start) is { } format)
            {
                if (ImageHeaders.Size(stream, format) is not var (width, height)) throw CaptureException.Unreadable(displayName);
                if ((long)width * height > MaxImagePixels) throw CaptureException.TooManyPixels(displayName);
                var (mime, extension) = ImageHeaders.Describe(format);
                result = new InspectedFile(ReferenceKind.Image, mime, extension, info.Length, "", width, height);
            }
            else if (start.IndexOf("%PDF-"u8) >= 0)
            {
                if (!pdfHasPages(path)) throw CaptureException.Unreadable(displayName);
                result = new InspectedFile(ReferenceKind.Pdf, "application/pdf", "pdf", info.Length, "", null, null);
            }
            else if (MarkdownExtensions.Contains(Path.GetExtension(displayName)) || MarkdownExtensions.Contains(Path.GetExtension(path)))
            {
                if (info.Length > MaxMarkdownBytes) throw CaptureException.TooLarge(displayName);
                stream.Position = 0;
                var bytes = new byte[info.Length];
                stream.ReadExactly(bytes);
                try { new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
                catch (DecoderFallbackException) { throw CaptureException.Unreadable(displayName); }
                result = new InspectedFile(ReferenceKind.Markdown, "text/markdown", "md", info.Length, "", null, null);
            }
            else
            {
                throw CaptureException.Unsupported(displayName);
            }

            stream.Position = 0;
            return result with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(stream)) };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw CaptureException.Unreadable(displayName);
        }
    }
}
