using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

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

    public static IReadOnlyList<string> SupportedExtensions { get; } =
        new[] { ".txt", ".md", ".csv", ".json", ".xml", ".html", ".htm", ".pdf", ".docx", ".xlsx" };

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

            default:
                throw new NotSupportedException($"The {extension} format is not supported.");
        }

        var text = builder.ToString().Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            var scanHint = extension == ".pdf"
                ? " The PDF may contain scanned images rather than embedded text; OCR is not performed by this feature."
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
                "The PDF contains no extractable text. It may be a scanned document; OCR is not performed, so use a text-based PDF or attach a selected page image.");

        return $"Extracted text from {pagesProcessed} of {count} PDF pages locally. Page layout may differ from the original document.";
    }

    private static string ExtractWord(string path, StringBuilder output, ref int paragraphsProcessed, ref bool truncated)
    {
        using var document = WordprocessingDocument.Open(path, isEditable: false);
        var body = document.MainDocumentPart?.Document?.Body
            ?? throw new InvalidDataException("The DOCX file has no readable main document body.");

        foreach (var paragraph in body.Descendants<Paragraph>())
        {
            if (output.Length >= MaximumTextCharacters)
            {
                truncated = true;
                break;
            }

            var paragraphText = string.Concat(paragraph.Descendants<DocumentFormat.OpenXml.Wordprocessing.Text>().Select(item => item.Text));
            if (string.IsNullOrWhiteSpace(paragraphText))
                continue;

            AppendLimited(output, paragraphText + Environment.NewLine, ref truncated);
            paragraphsProcessed++;
        }

        if (paragraphsProcessed == 0)
            throw new InvalidDataException("No readable paragraph text was found in the DOCX file.");

        return $"Extracted {paragraphsProcessed} Word paragraphs locally. Headers, footers, text boxes, comments and embedded objects may not be included.";
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
