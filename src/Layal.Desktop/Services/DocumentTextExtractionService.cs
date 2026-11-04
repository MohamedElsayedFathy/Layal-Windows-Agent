using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TesseractOCR;
using TesseractOCR.Enums;
using TesseractOCR.Pix;

namespace Layal.Services;

/// <summary>
/// Extracts bounded plain text locally from user-selected files. The result is not transmitted by this service.
/// Office macros and external relationships are never executed or followed.
/// </summary>
public sealed class DocumentTextExtractionService
{
    public const long MaximumFileBytes = 20L * 1024 * 1024;
    public const int MaximumTextCharacters = 10_000;
    public const int MaximumPdfPages = 25;
    public const int MaximumWorksheets = 20;
    public const int MaximumRowsPerWorksheet = 300;
    public const int MaximumSpreadsheetCells = 5_000;
    public const int MaximumArchiveEntries = 2_048;
    public const long MaximumArchiveExpandedBytes = 80L * 1024 * 1024;
    private const long MaximumArchiveEntryBytes = 32L * 1024 * 1024;
    private const int MaximumCompressionRatio = 500;

    public const int MaximumOcrPagePixels = 8_000_000;
    public const int MaximumOcrPageSidePixels = 3_000;
    public const int MaximumOcrSourcePixels = 16_000_000;
    public const int MaximumOcrSourceSidePixels = 6_000;
    public const int MaximumPdfImagesPerPage = 8;
    public const int MaximumOcrImageFrames = 25;
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff" };

    public static IReadOnlyList<string> SupportedExtensions { get; } =
        new[] { ".txt", ".md", ".csv", ".json", ".xml", ".html", ".htm", ".pdf", ".docx", ".xlsx", ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff" };

    /// <summary>
    /// Extracts bounded text locally. Images and PDF-embedded scans use the bundled Tesseract
    /// engine with Arabic and English models; no Windows package identity or OS language pack is required.
    /// </summary>
    public async Task<DocumentTextExtractionResult> ExtractFileAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A file path is required.", nameof(path));

        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(path);
        var extension = Path.GetExtension(fullPath).ToLowerInvariant();
        if (ImageExtensions.Contains(extension))
        {
            var imageInfo = new FileInfo(fullPath);
            if (!imageInfo.Exists)
                throw new FileNotFoundException("The selected image no longer exists.", fullPath);
            if (imageInfo.Length <= 0)
                throw new InvalidDataException("The selected image is empty.");
            if (imageInfo.Length > MaximumFileBytes)
                throw new InvalidDataException($"The selected image exceeds the {MaximumFileBytes / (1024 * 1024)} MB limit.");

            var imageBytes = await File.ReadAllBytesAsync(fullPath, cancellationToken);
            try
            {
                // Native Tesseract recognition is synchronous; keep it off the UI thread.
                // Cancellation is observed between frames/pages because a native call cannot be aborted safely.
                return await Task.Run(
                    () => ExtractImageBytesWithOcr(fullPath, imageBytes, cancellationToken),
                    CancellationToken.None);
            }
            finally
            {
                System.Array.Clear(imageBytes, 0, imageBytes.Length);
            }
        }

        if (extension == ".pdf")
        {
            DocumentTextExtractionResult textExtraction;
            try
            {
                textExtraction = await Task.Run(() => ExtractFile(fullPath), cancellationToken);
            }
            catch (InvalidDataException ex) when (
                ex.Message.StartsWith("The PDF contains no extractable text.", StringComparison.Ordinal))
            {
                textExtraction = new DocumentTextExtractionResult(
                    Path.GetFileName(fullPath), ".pdf", string.Empty, 0, false,
                    "No embedded PDF text layer was found; checking scanned page images with local OCR.");
            }

            return await Task.Run(
                () => SupplementPdfImagesWithOcr(fullPath, textExtraction, cancellationToken),
                CancellationToken.None);
        }

        return await Task.Run(() => ExtractFile(fullPath), cancellationToken);
    }

    public DocumentTextExtractionResult ExtractFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A file path is required.", nameof(path));

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The selected file no longer exists.", fullPath);

        var extension = Path.GetExtension(fullPath).ToLowerInvariant();
        if (!SupportedExtensions.Contains(extension, StringComparer.Ordinal))
            throw new NotSupportedException(
                $"The {extension} format is not supported. Use TXT, Markdown, CSV, JSON, XML, HTML, PDF, DOCX or XLSX.");

        var info = new FileInfo(fullPath);
        if (info.Length <= 0)
            throw new InvalidDataException("The selected file is empty.");
        if (info.Length > MaximumFileBytes)
            throw new InvalidDataException($"The selected file exceeds the {MaximumFileBytes / (1024 * 1024)} MB limit.");

        var builder = new StringBuilder(Math.Min(MaximumTextCharacters, (int)Math.Min(info.Length, MaximumTextCharacters)));
        var wasTruncated = false;
        var unitsProcessed = 0;
        string notice;

        switch (extension)
        {
            case ".txt":
            case ".md":
            case ".csv":
            case ".json":
                AppendLimited(builder, File.ReadAllText(fullPath, Encoding.UTF8), ref wasTruncated);
                unitsProcessed = 1;
                notice = "Plain text extracted locally.";
                break;

            case ".html":
            case ".htm":
                AppendLimited(builder, ExtractHtml(File.ReadAllText(fullPath, Encoding.UTF8)), ref wasTruncated);
                unitsProcessed = 1;
                notice = "HTML markup was removed locally; embedded images and scripts were not executed.";
                break;

            case ".xml":
                AppendLimited(builder, ExtractXmlText(fullPath), ref wasTruncated);
                unitsProcessed = 1;
                notice = "XML text nodes extracted locally; external entities are disabled.";
                break;

            case ".pdf":
                notice = ExtractPdf(fullPath, builder, ref unitsProcessed, ref wasTruncated);
                break;

            case ".docx":
                ValidateOfficePackage(fullPath);
                notice = ExtractWord(fullPath, builder, ref unitsProcessed, ref wasTruncated);
                break;

            case ".xlsx":
                ValidateOfficePackage(fullPath);
                notice = ExtractSpreadsheet(fullPath, builder, ref unitsProcessed, ref wasTruncated);
                break;

            case ".png":
            case ".jpg":
            case ".jpeg":
            case ".bmp":
            case ".tif":
            case ".tiff":
                throw new NotSupportedException("Image OCR uses the bundled local Tesseract engine and must be called asynchronously. Use ExtractFileAsync for image files.");

            default:
                throw new NotSupportedException($"The {extension} format is not supported.");
        }

        var text = builder.ToString().Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            var scanHint = extension == ".pdf"
                ? " The PDF may contain scanned images; use ExtractFileAsync for local OCR."
                : string.Empty;
            throw new InvalidDataException("No extractable text was found in the selected file." + scanHint);
        }

        if (wasTruncated)
            notice += $" Output truncated: extraction was limited to the first {MaximumTextCharacters:N0} characters or the per-format page/row limits.";
        notice += " Content is treated as untrusted data. Nothing is sent to an AI provider until you attach it and press Send.";

        return new DocumentTextExtractionResult(
            Path.GetFileName(fullPath),
            extension,
            text,
            unitsProcessed,
            wasTruncated,
            notice);
    }

    private static Engine CreateLocalOcrEngine()
    {
        var tessdataDirectory = Path.Combine(AppContext.BaseDirectory, "tessdata");
        var arabicModel = Path.Combine(tessdataDirectory, "ara.traineddata");
        var englishModel = Path.Combine(tessdataDirectory, "eng.traineddata");
        if (!File.Exists(arabicModel) || !File.Exists(englishModel))
        {
            throw new InvalidOperationException(
                "The bundled Arabic/English OCR models are missing. Run tools/Install-LayalOcrModels.ps1 from the source repository, or reinstall the complete Layal distribution. OCR stays local; no model or document is uploaded.");
        }

        try
        {
            // Bundled Tesseract models work in the unpackaged WPF installer; no MSIX identity or Windows OCR pack is required.
            return new Engine(tessdataDirectory, "ara+eng", EngineMode.LstmOnly);
        }
        catch (Exception ex) when (
            ex is DllNotFoundException or BadImageFormatException or TypeInitializationException)
        {
            throw new InvalidOperationException(
                "The local OCR engine could not load its native Windows runtime. Repair the Layal installation and ensure the Microsoft Visual C++ 2015-2022 x64 Redistributable is installed.",
                ex);
        }
    }

    private DocumentTextExtractionResult ExtractImageBytesWithOcr(
        string path,
        byte[] sourceBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var sourceStream = new MemoryStream(sourceBytes, writable: false);
        var decoder = BitmapDecoder.Create(
            sourceStream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnDemand);
        if (decoder.Frames.Count <= 0)
            throw new InvalidDataException("The selected image contains no readable frames.");

        var framesToProcess = Math.Min(decoder.Frames.Count, MaximumOcrImageFrames);
        var truncated = decoder.Frames.Count > framesToProcess;
        var output = new StringBuilder(Math.Min(MaximumTextCharacters, 2048));
        var processedFrames = 0;
        using var engine = CreateLocalOcrEngine();

        for (var frameIndex = 0; frameIndex < framesToProcess && output.Length < MaximumTextCharacters; frameIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = decoder.Frames[frameIndex];
            var width = frame.PixelWidth;
            var height = frame.PixelHeight;
            if (width <= 0 || height <= 0)
                continue;

            var scale = GetSafeOcrScale(width, height);
            BitmapSource boundedFrame = frame;
            if (scale < 0.999999)
                boundedFrame = new TransformedBitmap(frame, new ScaleTransform(scale, scale));

            byte[]? normalizedBytes = null;
            try
            {
                normalizedBytes = EncodePng(boundedFrame);
                var recognizedText = RecognizeImageBytes(normalizedBytes, engine, cancellationToken);
                if (!string.IsNullOrWhiteSpace(recognizedText))
                {
                    var label = framesToProcess > 1 ? $"[OCR image page {frameIndex + 1}]\n" : string.Empty;
                    AppendLimited(output, label + recognizedText.Trim() + "\n", ref truncated);
                }
                processedFrames++;
            }
            finally
            {
                if (normalizedBytes is not null)
                    System.Array.Clear(normalizedBytes, 0, normalizedBytes.Length);
            }
        }

        var text = output.ToString().Trim();
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidDataException(
                "The local OCR engine did not detect readable text in this image. Try a sharper, upright image with larger text.");

        var notice = $"Image text was recognized locally with bundled Arabic and English Tesseract models ({processedFrames} of {decoder.Frames.Count} frame(s)). Recognition may vary with scan quality, skew, handwriting, mixed right-to-left text, and tables; verify the preview before relying on names, dates, or numbers. Nothing is sent until you attach the reviewed text and press Send.";
        if (truncated)
            notice += $" Output was limited to {MaximumTextCharacters:N0} characters or {MaximumOcrImageFrames} image frames.";

        return new DocumentTextExtractionResult(
            Path.GetFileName(path),
            Path.GetExtension(path).ToLowerInvariant(),
            text,
            processedFrames,
            truncated,
            notice);
    }

    private DocumentTextExtractionResult SupplementPdfImagesWithOcr(
        string path,
        DocumentTextExtractionResult textExtraction,
        CancellationToken cancellationToken)
    {
        using var document = PdfDocument.Open(path);
        var pageCount = document.NumberOfPages;
        if (pageCount <= 0)
            throw new InvalidDataException("The PDF does not contain readable pages.");

        var pagesToInspect = Math.Min(pageCount, MaximumPdfPages);
        var output = new StringBuilder(textExtraction.Text);
        var truncated = textExtraction.WasTruncated || pageCount > pagesToInspect;
        var recognizedImages = 0;
        var skippedImages = 0;
        var candidateFound = false;
        Engine? engine = null;

        try
        {
            for (var pageNumber = 1; pageNumber <= pagesToInspect && output.Length < MaximumTextCharacters; pageNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = document.GetPage(pageNumber);
                var images = page.GetImages().Take(MaximumPdfImagesPerPage + 1).ToList();
                if (images.Count > MaximumPdfImagesPerPage)
                {
                    truncated = true;
                    images = images.Take(MaximumPdfImagesPerPage).ToList();
                }

                var imageIndex = 0;
                foreach (var pdfImage in images)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    imageIndex++;
                    // Accept ordinary 300-DPI A4 scans (roughly 8.7 MP) and
                    // resample them to the smaller OCR working bitmap below.
                    if (pdfImage.WidthInSamples <= 0 || pdfImage.HeightInSamples <= 0 ||
                        (long)pdfImage.WidthInSamples * pdfImage.HeightInSamples > MaximumOcrSourcePixels ||
                        pdfImage.WidthInSamples > MaximumOcrSourceSidePixels ||
                        pdfImage.HeightInSamples > MaximumOcrSourceSidePixels)
                    {
                        skippedImages++;
                        continue;
                    }

                    var imageBytes = TryGetPdfImageBytes(pdfImage);
                    if (imageBytes is null)
                    {
                        skippedImages++;
                        continue;
                    }

                    candidateFound = true;
                    byte[]? normalizedImageBytes = null;
                    try
                    {
                        // Scale standard high-DPI scan images before allocating the OCR bitmap.
                        var recognitionBytes = NormalizePdfImageForOcr(imageBytes, cancellationToken);
                        if (!ReferenceEquals(recognitionBytes, imageBytes))
                            normalizedImageBytes = recognitionBytes;

                        engine ??= CreateLocalOcrEngine();
                        var recognizedText = RecognizeImageBytes(recognitionBytes, engine, cancellationToken);
                        if (string.IsNullOrWhiteSpace(recognizedText))
                            continue;

                        var label = $"[OCR PDF page {pageNumber}, image {imageIndex}]\n";
                        AppendLimited(output, (output.Length > 0 ? "\n" : string.Empty) + label + recognizedText.Trim() + "\n", ref truncated);
                        recognizedImages++;
                    }
                    catch (InvalidDataException)
                    {
                        skippedImages++;
                    }
                    finally
                    {
                        if (normalizedImageBytes is not null)
                            System.Array.Clear(normalizedImageBytes, 0, normalizedImageBytes.Length);
                        System.Array.Clear(imageBytes, 0, imageBytes.Length);
                    }

                    if (output.Length >= MaximumTextCharacters)
                    {
                        truncated = true;
                        break;
                    }
                }
            }
        }
        catch (InvalidOperationException ex) when (candidateFound && engine is null)
        {
            if (string.IsNullOrWhiteSpace(textExtraction.Text))
                throw new InvalidOperationException(
                    "The PDF contains scanned images, but local OCR could not start. " + ex.Message,
                    ex);

            textExtraction = textExtraction with
            {
                Notice = textExtraction.Notice + " Scanned PDF images were detected but could not be OCRed locally: " + ex.Message
            };
        }
        finally
        {
            engine?.Dispose();
        }

        var text = output.ToString().Trim();
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidDataException(
                "No readable text layer or OCR-compatible embedded images were found in this PDF. The file may be vector-only, encrypted, damaged, or use an unsupported PDF image filter.");

        var notice = textExtraction.Notice;
        if (recognizedImages > 0)
            notice += $" Local OCR added text from {recognizedImages} embedded image(s) across up to {pagesToInspect} PDF page(s). Digital text was preserved alongside the OCR text.";
        if (skippedImages > 0)
            notice += $" {skippedImages} embedded image(s) were skipped because their format, dimensions, or decoding path exceeded the safe OCR limits.";
        if (pageCount > pagesToInspect)
            notice += $" Only the first {MaximumPdfPages} pages were inspected.";
        if (truncated)
            notice += $" Combined text was limited to {MaximumTextCharacters:N0} characters.";
        notice += " OCR ran locally. Nothing is sent to an AI provider until you review the preview, attach the extracted text, and press Send.";

        return new DocumentTextExtractionResult(
            textExtraction.FileName,
            ".pdf",
            text,
            Math.Max(textExtraction.UnitsProcessed, pagesToInspect),
            truncated,
            notice);
    }

    private static byte[] NormalizePdfImageForOcr(
        byte[] imageBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new MemoryStream(imageBytes, writable: false);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnDemand);
        if (decoder.Frames.Count == 0)
            throw new InvalidDataException("The embedded PDF image contains no readable frame.");

        var frame = decoder.Frames[0];
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 ||
            (long)frame.PixelWidth * frame.PixelHeight > MaximumOcrSourcePixels ||
            frame.PixelWidth > MaximumOcrSourceSidePixels ||
            frame.PixelHeight > MaximumOcrSourceSidePixels)
        {
            throw new InvalidDataException("The embedded PDF image exceeds the safe pre-resampling source limit.");
        }

        var scale = GetSafeOcrScale(frame.PixelWidth, frame.PixelHeight);
        if (scale >= 0.999999)
            return imageBytes;

        cancellationToken.ThrowIfCancellationRequested();
        var resized = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
        return EncodePng(resized);
    }

    private static byte[]? TryGetPdfImageBytes(IPdfImage pdfImage)
    {
        try
        {
            if (pdfImage.TryGetPng(out var pngBytes) && pngBytes is { Length: > 0 } &&
                pngBytes.Length <= MaximumFileBytes)
                return pngBytes;

            // DCTDecode image streams are already JPEG byte streams and can be passed to Leptonica directly.
            var rawBytes = pdfImage.RawBytes.ToArray();
            if (rawBytes.Length > 0 && rawBytes.Length <= MaximumFileBytes && IsJpeg(rawBytes))
                return rawBytes;
        }
        catch (Exception ex) when (
            ex is IOException or InvalidDataException or NotSupportedException or InvalidOperationException)
        {
            // A PDF may contain unsupported image filters. Other page content remains readable.
        }

        return null;
    }

    private static bool IsJpeg(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;

    private static string RecognizeImageBytes(
        byte[] imageBytes,
        Engine engine,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var image = TesseractOCR.Pix.Image.LoadFromMemory(imageBytes);
        if (image.Width <= 0 || image.Height <= 0 ||
            (long)image.Width * image.Height > MaximumOcrPagePixels ||
            image.Width > MaximumOcrPageSidePixels ||
            image.Height > MaximumOcrPageSidePixels)
        {
            throw new InvalidDataException(
                $"The decoded OCR image exceeds the safe limit ({MaximumOcrPageSidePixels:N0} pixels per side and {MaximumOcrPagePixels:N0} pixels total).");
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var page = engine.Process(image, PageSegMode.Auto);
        cancellationToken.ThrowIfCancellationRequested();
        return page.Text ?? string.Empty;
    }

    private static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        if (stream.Length <= 0 || stream.Length > MaximumFileBytes)
            throw new InvalidDataException("The normalized OCR image exceeds the safe in-memory image limit.");
        return stream.ToArray();
    }

    private static double GetSafeOcrScale(int width, int height)
    {
        var scaleForSide = Math.Min(
            (double)MaximumOcrPageSidePixels / width,
            (double)MaximumOcrPageSidePixels / height);
        var scaleForPixels = Math.Sqrt((double)MaximumOcrPagePixels / ((long)width * height));
        return Math.Min(1.0, Math.Min(scaleForSide, scaleForPixels));
    }

    private static string ExtractPdf(string path, StringBuilder output, ref int pagesProcessed, ref bool truncated)
    {
        using var document = PdfDocument.Open(path);
        var count = document.NumberOfPages;
        if (count <= 0)
            throw new InvalidDataException("The PDF does not contain readable pages.");

        var limit = Math.Min(count, MaximumPdfPages);
        for (var pageNumber = 1; pageNumber <= limit; pageNumber++)
        {
            if (output.Length >= MaximumTextCharacters)
            {
                truncated = true;
                break;
            }

            var page = document.GetPage(pageNumber);
            var text = ContentOrderTextExtractor.GetText(page);
            if (!string.IsNullOrWhiteSpace(text))
                AppendLimited(output, $"[PDF page {pageNumber}]\n{text}\n\n", ref truncated);
            pagesProcessed++;
        }

        if (count > limit)
            truncated = true;

        if (output.Length == 0)
            throw new InvalidDataException(
                "The PDF contains no extractable text. It may be scanned; use ExtractFileAsync to run local OCR on supported embedded page images.");

        return $"Extracted text from {pagesProcessed} of {count} PDF pages locally. Page layout may differ from the original document.";
    }

    private static string ExtractWord(string path, StringBuilder output, ref int unitsProcessed, ref bool truncated)
    {
        using var document = WordprocessingDocument.Open(path, isEditable: false);
        var body = document.MainDocumentPart?.Document?.Body
            ?? throw new InvalidDataException("The DOCX file has no readable main document body.");

        var tableIndex = 0;
        foreach (var block in body.ChildElements)
        {
            if (output.Length >= MaximumTextCharacters)
            {
                truncated = true;
                break;
            }

            if (block is Paragraph paragraph)
            {
                var paragraphText = GetWordParagraphText(paragraph);
                if (string.IsNullOrWhiteSpace(paragraphText))
                    continue;

                AppendLimited(output, paragraphText + Environment.NewLine, ref truncated);
                unitsProcessed++;
                continue;
            }

            if (block is not DocumentFormat.OpenXml.Wordprocessing.Table table)
                continue;

            tableIndex++;
            AppendLimited(output, $"[Word table {tableIndex}]\n", ref truncated);
            var rowNumber = 0;
            foreach (var row in table.Elements<TableRow>())
            {
                if (output.Length >= MaximumTextCharacters)
                {
                    truncated = true;
                    break;
                }

                rowNumber++;
                // Keep empty cells in place so column boundaries remain meaningful.
                var cells = row.Elements<TableCell>()
                    .Select(cell => string.Join(" ", cell.Descendants<Paragraph>()
                        .Select(GetWordParagraphText)
                        .Where(value => !string.IsNullOrWhiteSpace(value))))
                    .Select(value => value.Replace("\r", " ").Replace("\n", " ").Trim())
                    .ToList();
                if (cells.Count == 0 || cells.All(string.IsNullOrWhiteSpace))
                    continue;

                AppendLimited(output,
                    $"Row {rowNumber}: {string.Join(" | ", cells)}{Environment.NewLine}",
                    ref truncated);
                unitsProcessed++;
            }

            AppendLimited(output, Environment.NewLine, ref truncated);
        }

        if (unitsProcessed == 0)
            throw new InvalidDataException("No readable paragraph or table text was found in the DOCX file.");

        return $"Extracted {unitsProcessed} Word paragraphs and table rows locally. DOCX table row and cell boundaries are preserved. Headers, footers, text boxes, comments and embedded objects may not be included.";
    }

    private static string GetWordParagraphText(Paragraph paragraph)
    {
        var text = new StringBuilder();
        foreach (var element in paragraph.Descendants<OpenXmlElement>())
        {
            if (element is DocumentFormat.OpenXml.Wordprocessing.Text runText)
                text.Append(runText.Text);
            else if (element is DocumentFormat.OpenXml.Wordprocessing.Break)
                text.AppendLine();
            else if (element is TabChar)
                text.Append('\t');
        }

        return text.ToString();
    }

    private static string ExtractSpreadsheet(string path, StringBuilder output, ref int worksheetsProcessed, ref bool truncated)
    {
        using var document = SpreadsheetDocument.Open(path, isEditable: false);
        var workbook = document.WorkbookPart?.Workbook
            ?? throw new InvalidDataException("The XLSX file has no readable workbook.");
        var workbookPart = document.WorkbookPart!;
        var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable;
        var sheets = workbook.Sheets?.Elements<Sheet>().ToList()
            ?? throw new InvalidDataException("The XLSX file contains no worksheet list.");

        var cellsVisited = 0;
        var nonEmptyCellCount = 0;
        foreach (var sheet in sheets.Take(MaximumWorksheets))
        {
            if (output.Length >= MaximumTextCharacters || cellsVisited >= MaximumSpreadsheetCells)
            {
                truncated = true;
                break;
            }

            if (string.IsNullOrWhiteSpace(sheet.Id?.Value))
                continue;
            if (workbookPart.GetPartById(sheet.Id!.Value) is not WorksheetPart worksheetPart)
                continue;

            AppendLimited(output, $"[Worksheet: {sheet.Name?.Value ?? "Sheet"}]\n", ref truncated);
            var worksheet = worksheetPart.Worksheet;
            if (worksheet is null)
            {
                truncated = true;
                continue;
            }
            var rows = worksheet.GetFirstChild<SheetData>()?.Elements<Row>().Take(MaximumRowsPerWorksheet + 1).ToList()
                ?? new List<Row>();

            foreach (var row in rows.Take(MaximumRowsPerWorksheet))
            {
                if (output.Length >= MaximumTextCharacters || cellsVisited >= MaximumSpreadsheetCells)
                {
                    truncated = true;
                    break;
                }

                var values = new List<string>();
                foreach (var cell in row.Elements<Cell>())
                {
                    if (cellsVisited >= MaximumSpreadsheetCells || output.Length >= MaximumTextCharacters)
                    {
                        truncated = true;
                        break;
                    }

                    cellsVisited++;
                    var value = GetCellText(cell, sharedStrings);
                    if (string.IsNullOrWhiteSpace(value))
                        continue;
                    var reference = cell.CellReference?.Value;
                    values.Add(string.IsNullOrWhiteSpace(reference) ? value : $"{reference}: {value}");
                    nonEmptyCellCount++;
                }

                if (values.Count > 0)
                    AppendLimited(output, string.Join(" | ", values) + Environment.NewLine, ref truncated);
            }

            worksheetsProcessed++;
            if (rows.Count > MaximumRowsPerWorksheet)
                truncated = true;
            AppendLimited(output, Environment.NewLine, ref truncated);
        }

        if (sheets.Count > MaximumWorksheets)
            truncated = true;
        if (worksheetsProcessed == 0 || nonEmptyCellCount == 0)
            throw new InvalidDataException("No readable cell values were found in the XLSX workbook.");

        return $"Extracted {nonEmptyCellCount} non-empty Excel cells across {worksheetsProcessed} worksheets locally. Formula results are read only when saved in the workbook; formulas are not executed.";
    }

    private static string GetCellText(Cell cell, SharedStringTable? sharedStrings)
    {
        var raw = cell.CellValue?.Text ?? cell.InlineString?.InnerText ?? string.Empty;
        if (cell.DataType?.Value == CellValues.SharedString &&
            int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var index) &&
            sharedStrings is not null)
        {
            return (uint)index < (uint)sharedStrings.ChildElements.Count
                ? sharedStrings.ElementAt(index).InnerText
                : string.Empty;
        }

        if (cell.DataType?.Value == CellValues.Boolean)
            return raw == "1" ? "TRUE" : raw == "0" ? "FALSE" : raw;

        return raw;
    }

    private static void ValidateOfficePackage(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            if (archive.Entries.Count == 0 || archive.Entries.Count > MaximumArchiveEntries)
                throw new InvalidDataException("The Office package has an unsupported number of archive entries.");

            long expandedBytes = 0;
            foreach (var entry in archive.Entries)
            {
                if (entry.Length < 0 || entry.Length > MaximumArchiveEntryBytes)
                    throw new InvalidDataException("An Office package entry exceeds the permitted expanded size.");

                expandedBytes = checked(expandedBytes + entry.Length);
                if (expandedBytes > MaximumArchiveExpandedBytes)
                    throw new InvalidDataException("The Office package expands beyond the safe 80 MB extraction limit.");

                if (entry.Length > 1_000_000 &&
                    (entry.CompressedLength == 0 ||
                     entry.Length / Math.Max(1L, entry.CompressedLength) > MaximumCompressionRatio))
                    throw new InvalidDataException("The Office package has an unsafe compression ratio.");

                if (entry.FullName.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException("Macro-enabled Office content is not opened. Save a macro-free DOCX/XLSX copy first.");
            }
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (NotSupportedException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OverflowException)
        {
            throw new InvalidDataException("The selected Office document is not a safe, readable Open XML package.", ex);
        }
    }

    private static string ExtractXmlText(string path)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumFileBytes,
            MaxCharactersFromEntities = 0
        };
        using var reader = XmlReader.Create(path, settings);
        var document = XDocument.Load(reader, LoadOptions.None);
        return string.Join(Environment.NewLine,
            document.DescendantNodes()
                .OfType<XText>()
                .Select(node => node.Value.Trim())
                .Where(value => value.Length > 0));
    }

    private static string ExtractHtml(string source)
    {
        var timeout = TimeSpan.FromMilliseconds(500);
        source = Regex.Replace(source, @"(?is)<(script|style|noscript)\b[^>]*>.*?</\1\s*>", " ",
            RegexOptions.CultureInvariant, timeout);
        source = Regex.Replace(source, @"(?is)<br\s*/?>|</(p|div|li|tr|h[1-6]|section|article)\s*>", "\n",
            RegexOptions.CultureInvariant, timeout);
        source = Regex.Replace(source, @"(?is)<[^>]+>", " ",
            RegexOptions.CultureInvariant, timeout);
        return WebUtility.HtmlDecode(source);
    }

    private static void AppendLimited(StringBuilder output, string value, ref bool truncated)
    {
        if (string.IsNullOrEmpty(value))
            return;
        var remaining = MaximumTextCharacters - output.Length;
        if (remaining <= 0)
        {
            truncated = true;
            return;
        }

        if (value.Length <= remaining)
        {
            output.Append(value);
            return;
        }

        output.Append(value.AsSpan(0, remaining));
        truncated = true;
    }
}

public sealed record DocumentTextExtractionResult(
    string FileName,
    string Extension,
    string Text,
    int UnitsProcessed,
    bool WasTruncated,
    string Notice);
