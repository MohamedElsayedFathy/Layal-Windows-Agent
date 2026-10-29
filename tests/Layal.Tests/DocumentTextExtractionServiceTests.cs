using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Layal.Tests;

[TestClass]
public sealed class DocumentTextExtractionServiceTests
{
    private readonly DocumentTextExtractionService _service = new();

    [TestMethod]
    public void TextFilesAreReadLocallyAndBounded()
    {
        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "notes.txt");
        File.WriteAllText(path, "line one\nline two");

        var result = _service.ExtractFile(path);

        Assert.AreEqual("notes.txt", result.FileName);
        Assert.IsTrue(result.Text.Contains("line one", StringComparison.Ordinal));
        Assert.IsTrue(result.Text.Contains("line two", StringComparison.Ordinal));
        Assert.IsFalse(result.WasTruncated);
        Assert.IsTrue(result.Notice.Contains("locally", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void OversizedTextExtractionIsTruncatedAtTheConfiguredLimit()
    {
        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "large.txt");
        File.WriteAllText(path, new string('x', DocumentTextExtractionService.MaximumTextCharacters + 500));

        var result = _service.ExtractFile(path);

        Assert.AreEqual(DocumentTextExtractionService.MaximumTextCharacters, result.Text.Length);
        Assert.IsTrue(result.WasTruncated);
        Assert.IsTrue(result.Notice.Contains("truncated", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void UnsupportedAndEmptyFilesAreRejected()
    {
        using var temp = new TestTempDirectory();
        var unsupported = Path.Combine(temp.Path, "legacy.doc");
        File.WriteAllText(unsupported, "not a docx");
        Assert.ThrowsExactly<NotSupportedException>(() => _service.ExtractFile(unsupported));

        var empty = Path.Combine(temp.Path, "empty.txt");
        File.WriteAllText(empty, string.Empty);
        Assert.ThrowsExactly<InvalidDataException>(() => _service.ExtractFile(empty));
    }

    [TestMethod]
    public void HtmlExtractionRemovesScriptAndStyleContent()
    {
        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "page.html");
        File.WriteAllText(path, "<html><body><h1>Visible title</h1><script>secret_command()</script><style>.hidden{}</style><p>Visible body</p></body></html>");

        var result = _service.ExtractFile(path);

        Assert.IsTrue(result.Text.Contains("Visible title", StringComparison.Ordinal));
        Assert.IsTrue(result.Text.Contains("Visible body", StringComparison.Ordinal));
        Assert.IsFalse(result.Text.Contains("secret_command", StringComparison.Ordinal));
        Assert.IsFalse(result.Text.Contains(".hidden", StringComparison.Ordinal));
    }

    [TestMethod]
    public void XmlExternalEntityDocumentsAreRejected()
    {
        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "xxe.xml");
        File.WriteAllText(path, "<!DOCTYPE root [<!ENTITY secret SYSTEM \"file:///c:/windows/win.ini\">]><root>&secret;</root>");

        Assert.ThrowsExactly<System.Xml.XmlException>(() => _service.ExtractFile(path));
    }

    [TestMethod]
    public void WordDocumentsExtractParagraphTextWithoutExecutingMacros()
    {
        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "notes.docx");
        using (var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new W.Document(new W.Body(
                new W.Paragraph(new W.Run(new W.Text("Word document sentinel"))),
                new W.Paragraph(new W.Run(new W.Text("Second paragraph")))));
            main.Document.Save();
        }

        var result = _service.ExtractFile(path);

        Assert.IsTrue(result.Text.Contains("Word document sentinel", StringComparison.Ordinal));
        Assert.IsTrue(result.Text.Contains("Second paragraph", StringComparison.Ordinal));
        Assert.AreEqual(2, result.UnitsProcessed);
    }

    [TestMethod]
    public void SpreadsheetExtractionReadsCellValuesAndSheetNames()
    {
        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "values.xlsx");
        using (var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new S.Workbook();
            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            worksheetPart.Worksheet = new S.Worksheet(new S.SheetData(
                new S.Row(
                    new S.Cell
                    {
                        CellReference = "A1",
                        DataType = S.CellValues.InlineString,
                        InlineString = new S.InlineString(new S.Text("Revenue"))
                    },
                    new S.Cell
                    {
                        CellReference = "B1",
                        DataType = S.CellValues.Number,
                        CellValue = new S.CellValue("27")
                    })));
            workbookPart.Workbook.AppendChild(new S.Sheets(
                new S.Sheet
                {
                    Id = workbookPart.GetIdOfPart(worksheetPart),
                    SheetId = 1U,
                    Name = "Summary"
                }));
            worksheetPart.Worksheet.Save();
            workbookPart.Workbook.Save();
        }

        var result = _service.ExtractFile(path);

        Assert.IsTrue(result.Text.Contains("Summary", StringComparison.Ordinal));
        Assert.IsTrue(result.Text.Contains("A1: Revenue", StringComparison.Ordinal));
        Assert.IsTrue(result.Text.Contains("B1: 27", StringComparison.Ordinal));
        Assert.AreEqual(1, result.UnitsProcessed);
    }

    [TestMethod]
    public void MacroPayloadInOpenXmlContainerIsRejectedBeforeDocumentParsing()
    {
        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "macro.xlsx");
        using (var file = File.Create(path))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        using (var entry = archive.CreateEntry("xl/vbaProject.bin").Open())
        {
            entry.WriteByte(0x01);
        }

        Assert.ThrowsExactly<NotSupportedException>(() => _service.ExtractFile(path));
    }

    [TestMethod]
    public void TextBasedPdfCanBeReadButScannedPdfIsNotClaimedAsOcr()
    {
        using var temp = new TestTempDirectory();
        var textPath = Path.Combine(temp.Path, "text.pdf");
        using (var builder = new PdfDocumentBuilder())
        {
            var font = builder.AddStandard14Font(Standard14Font.Helvetica);
            var page = builder.AddPage(PageSize.A4);
            page.AddText("PDF extraction sentinel", 12, new PdfPoint(40, 700), font);
            File.WriteAllBytes(textPath, builder.Build());
        }

        var result = _service.ExtractFile(textPath);
        Assert.IsTrue(result.Text.Contains("PDF extraction sentinel", StringComparison.Ordinal));
        Assert.AreEqual(1, result.UnitsProcessed);

        var scannedLikePath = Path.Combine(temp.Path, "scanned-like.pdf");
        using (var builder = new PdfDocumentBuilder())
        {
            builder.AddPage(PageSize.A4);
            File.WriteAllBytes(scannedLikePath, builder.Build());
        }
        var exception = Assert.ThrowsExactly<InvalidDataException>(() => _service.ExtractFile(scannedLikePath));
        StringAssert.Contains(exception.Message, "OCR is not performed");
    }
}
