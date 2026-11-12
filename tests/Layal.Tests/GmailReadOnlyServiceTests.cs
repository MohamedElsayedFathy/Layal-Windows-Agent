using System.Linq;
using Layal.Models;
using System.Threading.Tasks;
using System.Threading;
using System.Text.Json;
using System.Net.Http;
using System.Net;
using System.IO;
using System.Collections.Generic;
using System;
using System.Security.Cryptography;
using System.Text;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class GmailReadOnlyServiceTests
{
    [TestMethod]
    public void OAuthClientIdMustBeAnExpectedGoogleDesktopClientId()
    {
        const string clientId = "123456789012-abcdefghijklmnop.apps.googleusercontent.com";
        Assert.AreEqual(clientId, GmailReadOnlyService.ValidateOAuthClientId(clientId));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            GmailReadOnlyService.ValidateOAuthClientId("not-a-client-id"));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            GmailReadOnlyService.ValidateOAuthClientId("paste-your-client-secret-here"));
    }

    [TestMethod]
    public void PkceVerifierIsRandomAndChallengeUsesSha256Base64Url()
    {
        var first = GmailReadOnlyService.CreatePkceVerifier();
        var second = GmailReadOnlyService.CreatePkceVerifier();

        Assert.AreEqual(43, first.Length);
        Assert.AreEqual(43, second.Length);
        Assert.AreNotEqual(first, second);
        StringAssert.Matches(first, new System.Text.RegularExpressions.Regex("^[A-Za-z0-9_-]+$"));

        var expected = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(first)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.AreEqual(expected, GmailReadOnlyService.CreatePkceChallenge(first));
        Assert.AreNotEqual(first, GmailReadOnlyService.CreatePkceChallenge(first));
        Assert.ThrowsExactly<ArgumentException>(() => GmailReadOnlyService.CreatePkceChallenge("short"));
    }

    [TestMethod]
    public void HtmlBodyIsConvertedToPlainTextWithoutExecutableOrRemoteContent()
    {
        var converted = GmailReadOnlyService.ConvertHtmlToPlainText(
            "<html><head><style>.secret{display:none}</style></head><body>" +
            "<h1>Hello &amp; welcome</h1><script>stealCredentials()</script>" +
            "<iframe src='https://tracker.example'></iframe><form>Send password</form>" +
            "<p>Second line</p></body></html>");

        StringAssert.Contains(converted, "Hello & welcome");
        StringAssert.Contains(converted, "Second line");
        Assert.IsFalse(converted.Contains("stealCredentials", StringComparison.Ordinal));
        Assert.IsFalse(converted.Contains("tracker.example", StringComparison.Ordinal));
        Assert.IsFalse(converted.Contains("Send password", StringComparison.Ordinal));
        Assert.IsFalse(converted.Contains(".secret", StringComparison.Ordinal));
        Assert.IsTrue(converted.Length <= GmailReadOnlyService.MaximumBodyCharacters);
    }

    [TestMethod]
    public void HtmlBodyTruncationIncludesTheEllipsisInsideTheConfiguredLimit()
    {
        var source = new string('x', GmailReadOnlyService.MaximumBodyCharacters + 100);
        var converted = GmailReadOnlyService.ConvertHtmlToPlainText("<p>" + source + "</p>");

        Assert.AreEqual(GmailReadOnlyService.MaximumBodyCharacters, converted.Length);
        Assert.AreEqual('…', converted[^1]);

        var shortText = GmailReadOnlyService.ConvertHtmlToPlainText("<p>short message</p>");
        Assert.AreEqual("short message", shortText);
    }

    [TestMethod]
    public async Task ListMessagesAsyncUsesReadOnlyGetRequestsAndCapsTheRequestedPageSize()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("The Gmail credential store uses Windows DPAPI.");

        using var temp = new TestTempDirectory();
        var credentialPath = Path.Combine(temp.Path, "gmail.dpapi");
        new ProtectedJsonStore<GmailOAuthCredential>(credentialPath).Save(new GmailOAuthCredential
        {
            ClientId = "123456789012-abcdefghijklmnop.apps.googleusercontent.com",
            RefreshToken = "test-refresh-token"
        });

        using var handler = new GmailListStubHandler();
        using var client = new HttpClient(handler);
        var service = new GmailReadOnlyService(credentialPath, client);

        var messages = await service.ListMessagesAsync("from:sender@example.com", 999);

        Assert.AreEqual(2, messages.Count);
        Assert.AreEqual("message-1", messages[0].Id);
        Assert.AreEqual("sender@example.com", messages[0].From);
        Assert.AreEqual("Subject one", messages[0].Subject);
        Assert.IsNotNull(messages[0].ReceivedAt);
        Assert.IsTrue(handler.GmailRequests.All(request => request.Method == HttpMethod.Get),
            "Gmail mailbox reads must use GET only; the OAuth token exchange is the only POST request.");
        Assert.IsTrue(handler.ListRequestQuery.Contains("maxResults=25", StringComparison.Ordinal));
        Assert.IsTrue(handler.ListRequestQuery.Contains("from%3Asender%40example.com", StringComparison.Ordinal));
        Assert.AreEqual(2, handler.MetadataRequests);
    }

    [TestMethod]
    public async Task ReadMessageAsyncKeepsCorruptOversizedAndUnsupportedAttachmentsIsolated()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("The Gmail credential store uses Windows DPAPI and document preview uses Windows APIs.");

        using var temp = new TestTempDirectory();
        var credentialPath = Path.Combine(temp.Path, "gmail.dpapi");
        new ProtectedJsonStore<GmailOAuthCredential>(credentialPath).Save(new GmailOAuthCredential
        {
            ClientId = "123456789012-abcdefghijklmnop.apps.googleusercontent.com",
            RefreshToken = "test-refresh-token"
        });

        var oversizedBytes = new byte[GmailReadOnlyService.MaximumAttachmentBytes + 100];
        var messageJson = JsonSerializer.Serialize(new
        {
            id = "message-corrupt",
            threadId = "thread-corrupt",
            snippet = "A message with untrusted attachments",
            internalDate = "1790000000000",
            payload = new
            {
                headers = new[]
                {
                    new { name = "From", value = "sender@example.com" },
                    new { name = "Subject", value = "Corrupt attachment isolation" }
                },
                parts = new object[]
                {
                    new
                    {
                        filename = "corrupt.docx",
                        mimeType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                        body = new { size = 12, data = Base64Url("not a valid Office package") }
                    },
                    new
                    {
                        filename = "oversized.txt",
                        mimeType = "text/plain",
                        // Deliberately inconsistent metadata: the decoded bytes exceed the limit.
                        body = new { size = 12, data = Base64Url(oversizedBytes) }
                    },
                    new
                    {
                        filename = "payload.exe",
                        mimeType = "text/plain",
                        body = new { size = 20, attachmentId = "must-not-download" }
                    }
                }
            }
        });

        var previousPreviews = Directory.EnumerateFiles(Path.GetTempPath(), "layal-gmail-preview-*")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var handler = new GmailInlineAttachmentHandler(messageJson, "message-corrupt");
        using var client = new HttpClient(handler);
        var service = new GmailReadOnlyService(credentialPath, client);

        var detail = await service.ReadMessageAsync("message-corrupt");

        Assert.AreEqual("Corrupt attachment isolation", detail.Subject);
        Assert.AreEqual(3, detail.Attachments!.Count);
        StringAssert.Contains(detail.Attachments[0].Status, "safely parsed");
        StringAssert.Contains(detail.Attachments[1].Status, "Not previewed");
        StringAssert.Contains(detail.Attachments[2].Status, "Not previewed");
        Assert.IsFalse(handler.RequestedPaths.Any(path => path.Contains("must-not-download", StringComparison.Ordinal)));
        Assert.AreEqual(0, Directory.EnumerateFiles(Path.GetTempPath(), "layal-gmail-preview-*")
            .Count(path => !previousPreviews.Contains(path)),
            "A corrupt attachment must not leave its private temporary preview file behind.");
    }

    [TestMethod]
    public void AttachmentPreviewAllowsOnlyBoundedTextFormatsAndSanitizesNames()
    {
        Assert.IsTrue(GmailReadOnlyService.IsSupportedTextAttachment("notes.txt", "text/plain"));
        Assert.IsTrue(GmailReadOnlyService.IsSupportedTextAttachment("table.csv", "text/csv"));
        Assert.IsTrue(GmailReadOnlyService.IsSupportedTextAttachment("page.html", "text/html"));
        Assert.IsFalse(GmailReadOnlyService.IsSupportedTextAttachment("payload.exe", "application/octet-stream"));
        Assert.IsFalse(GmailReadOnlyService.IsSupportedTextAttachment("payload.exe", "text/plain"),
            "A sender-controlled MIME type must not override a non-text filename extension.");
        Assert.IsFalse(GmailReadOnlyService.IsSupportedTextAttachment("archive.zip", "application/zip"));
        Assert.IsTrue(GmailReadOnlyService.IsSupportedDocumentAttachment("report.pdf", "application/pdf"));
        Assert.IsTrue(GmailReadOnlyService.IsSupportedDocumentAttachment("report.docx", "application/octet-stream"),
            "The allow-listed filename extension is authoritative when MIME metadata is sender-controlled.");
        Assert.IsTrue(GmailReadOnlyService.IsSupportedDocumentAttachment("budget.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"));
        Assert.IsTrue(GmailReadOnlyService.IsSupportedImageAttachment("receipt.png"));
        Assert.IsTrue(GmailReadOnlyService.IsSupportedImageAttachment("scan.tiff"));
        Assert.IsFalse(GmailReadOnlyService.IsSupportedImageAttachment("payload.webp"));
        Assert.IsFalse(GmailReadOnlyService.IsSupportedImageAttachment("payload.exe"));
        Assert.IsFalse(GmailReadOnlyService.IsSupportedDocumentAttachment("payload.exe", "application/pdf"));
        Assert.IsFalse(GmailReadOnlyService.IsSupportedDocumentAttachment("archive.zip", "application/zip"));
        Assert.IsTrue(GmailReadOnlyService.IsSupportedTextAttachment(string.Empty, "text/plain"),
            "An extensionless attachment may be previewed only when its MIME type is an approved text type.");

        var safe = GmailReadOnlyService.SanitizeAttachmentFileName(@"C:\\private\\..\\report.txt");
        Assert.IsFalse(safe.Contains('\\'));
        Assert.IsFalse(safe.Contains('/'));
        Assert.IsFalse(safe.Contains(':'));
        Assert.IsTrue(safe.EndsWith("report.txt", StringComparison.Ordinal));
        Assert.IsTrue(safe.Length <= 120);
    }

    [TestMethod]
    public async Task ReadMessageAsyncConvertsHtmlAndPreviewsOnlySmallTextAttachments()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("The Gmail credential store uses Windows DPAPI.");

        using var temp = new TestTempDirectory();
        var credentialPath = System.IO.Path.Combine(temp.Path, "gmail.dpapi");
        new ProtectedJsonStore<GmailOAuthCredential>(credentialPath).Save(new GmailOAuthCredential
        {
            ClientId = "123456789012-abcdefghijklmnop.apps.googleusercontent.com",
            RefreshToken = "test-refresh-token"
        });

        var inlineText = "Inline attachment text";
        var remoteText = "Remote markdown attachment";
        var longPreview = new string('p', GmailReadOnlyService.MaximumAttachmentPreviewCharacters / 3);
        var html = "<html><body><p>HTML only body</p><script>bad()</script><style>.x{}</style></body></html>";
        var message = JsonSerializer.Serialize(new
        {
            id = "message-1",
            threadId = "thread-1",
            internalDate = "1793700000000",
            payload = new
            {
                mimeType = "multipart/mixed",
                headers = new[]
                {
                    new { name = "From", value = "sender@example.com" },
                    new { name = "To", value = "recipient@example.com" },
                    new { name = "Subject", value = "HTML and attachments" }
                },
                parts = new object[]
                {
                    new
                    {
                        mimeType = "text/html",
                        filename = "",
                        body = new { data = Base64Url(html) }
                    },
                    new
                    {
                        mimeType = "text/plain",
                        filename = "notes.txt",
                        body = new { size = inlineText.Length, data = Base64Url(inlineText) }
                    },
                    new
                    {
                        mimeType = "text/markdown",
                        filename = "readme.md",
                        body = new { size = remoteText.Length, attachmentId = "attachment-1" }
                    },
                    new
                    {
                        mimeType = "text/plain",
                        filename = "one.txt",
                        body = new { size = longPreview.Length, data = Base64Url(longPreview) }
                    },
                    new
                    {
                        mimeType = "text/plain",
                        filename = "two.txt",
                        body = new { size = longPreview.Length, data = Base64Url(longPreview) }
                    },
                    new
                    {
                        mimeType = "text/plain",
                        filename = "three.txt",
                        body = new { size = longPreview.Length, data = Base64Url(longPreview) }
                    },
                    new
                    {
                        mimeType = "text/plain",
                        filename = "four.txt",
                        body = new { size = longPreview.Length, data = Base64Url(longPreview) }
                    },
                    new
                    {
                        mimeType = "application/octet-stream",
                        filename = "payload.exe",
                        body = new { size = 32, attachmentId = "must-not-download" }
                    },
                    new
                    {
                        mimeType = "text/csv",
                        filename = "large.csv",
                        body = new { size = GmailReadOnlyService.MaximumAttachmentBytes + 1, attachmentId = "too-large" }
                    }
                }
            }
        });

        using var handler = new GmailApiStubHandler(message, remoteText);
        using var client = new HttpClient(handler);
        var service = new GmailReadOnlyService(credentialPath, client);

        var detail = await service.ReadMessageAsync("message-1");

        StringAssert.Contains(detail.PlainTextBody, "HTML only body");
        Assert.IsFalse(detail.PlainTextBody.Contains("bad()", StringComparison.Ordinal));
        Assert.IsFalse(detail.PlainTextBody.Contains(".x{", StringComparison.Ordinal));
        Assert.AreEqual(8, detail.Attachments!.Count);
        Assert.AreEqual(inlineText, detail.Attachments[0].PreviewText);
        Assert.AreEqual(remoteText, detail.Attachments[1].PreviewText);
        StringAssert.Contains(detail.Attachments[0].Status, "available");
        StringAssert.Contains(detail.Attachments[5].Status, "preview budget");
        StringAssert.Contains(detail.Attachments[6].Status, "Not previewed");
        StringAssert.Contains(detail.Attachments[7].Status, "Not previewed");
        Assert.IsTrue(detail.Attachments.Sum(item => item.PreviewText.Length) <=
            GmailReadOnlyService.MaximumAttachmentPreviewCharacters,
            "Total text previews across all message attachments must stay within the aggregate budget.");
        Assert.IsTrue(detail.Attachments.Take(5).All(item =>
            item.PreviewText.Length <= GmailReadOnlyService.MaximumAttachmentPreviewCharacters));
        Assert.IsTrue(detail.Attachments.Single(item => item.FileName == "four.txt").PreviewText.Length < longPreview.Length,
            "The final text attachment should be truncated when the aggregate preview budget is exhausted.");
        Assert.AreEqual(1, handler.AttachmentRequests);
        Assert.IsFalse(handler.RequestedPaths.Any(path => path.Contains("must-not-download", StringComparison.Ordinal)));
        Assert.IsFalse(handler.RequestedPaths.Any(path => path.Contains("too-large", StringComparison.Ordinal)));
    }


    [TestMethod]
    public async Task ReadMessageAsyncExtractsDocxAttachmentLocallyWithoutSavingItToWorkspace()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("The Gmail credential store uses Windows DPAPI and document preview uses Windows APIs.");

        using var temp = new TestTempDirectory();
        var credentialPath = Path.Combine(temp.Path, "gmail.dpapi");
        new ProtectedJsonStore<GmailOAuthCredential>(credentialPath).Save(new GmailOAuthCredential
        {
            ClientId = "123456789012-abcdefghijklmnop.apps.googleusercontent.com",
            RefreshToken = "test-refresh-token"
        });

        var existingTempPreviews = Directory.EnumerateFiles(Path.GetTempPath(), "layal-gmail-preview-*")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        byte[] docxBytes;
        using (var package = new MemoryStream())
        {
            using (var document = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(
                       package,
                       DocumentFormat.OpenXml.WordprocessingDocumentType.Document,
                       true))
            {
                var main = document.AddMainDocumentPart();
                main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(
                    new DocumentFormat.OpenXml.Wordprocessing.Body(
                        new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                            new DocumentFormat.OpenXml.Wordprocessing.Run(
                                new DocumentFormat.OpenXml.Wordprocessing.Text("Gmail document preview sentinel")))));
                main.Document.Save();
            }
            docxBytes = package.ToArray();
        }

        var messageJson = JsonSerializer.Serialize(new
        {
            id = "message-docx",
            threadId = "thread-docx",
            snippet = "Document attachment",
            internalDate = "1790000000000",
            payload = new
            {
                headers = new[]
                {
                    new { name = "From", value = "sender@example.com" },
                    new { name = "Subject", value = "Document preview" }
                },
                parts = new[]
                {
                    new
                    {
                        filename = "report.docx",
                        mimeType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                        body = new { size = docxBytes.Length, data = Base64Url(docxBytes) }
                    }
                }
            }
        });

        using var http = new HttpClient(new GmailInlineAttachmentHandler(messageJson));
        var service = new GmailReadOnlyService(credentialPath, http);
        var detail = await service.ReadMessageAsync("message-docx");

        Assert.AreEqual(1, detail.Attachments!.Count);
        StringAssert.Contains(detail.Attachments[0].Status, "Local document text preview available");
        StringAssert.Contains(detail.Attachments[0].PreviewText, "Gmail document preview sentinel");
        var leakedPreviews = Directory.EnumerateFiles(Path.GetTempPath(), "layal-gmail-preview-*")
            .Where(path => !existingTempPreviews.Contains(path)).ToArray();
        Assert.AreEqual(0, leakedPreviews.Length,
            "Gmail document bytes must not be retained after the local preview is extracted.");
    }


    [TestMethod]
    public async Task ReadMessageAsyncExtractsPdfAndXlsxAttachmentsLocallyAndDeletesTemporaryFiles()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("The Gmail credential store uses Windows DPAPI and document preview uses Windows APIs.");

        using var temp = new TestTempDirectory();
        var credentialPath = Path.Combine(temp.Path, "gmail.dpapi");
        new ProtectedJsonStore<GmailOAuthCredential>(credentialPath).Save(new GmailOAuthCredential
        {
            ClientId = "123456789012-abcdefghijklmnop.apps.googleusercontent.com",
            RefreshToken = "test-refresh-token"
        });

        byte[] pdfBytes;
        using (var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder())
        {
            var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
            var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
            page.AddText("Gmail PDF preview sentinel", 12, new UglyToad.PdfPig.Core.PdfPoint(40, 700), font);
            pdfBytes = builder.Build();
        }

        byte[] xlsxBytes;
        using (var package = new MemoryStream())
        {
            using (var workbook = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Create(
                       package,
                       DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook,
                       true))
            {
                var workbookPart = workbook.AddWorkbookPart();
                workbookPart.Workbook = new DocumentFormat.OpenXml.Spreadsheet.Workbook();
                var worksheetPart = workbookPart.AddNewPart<DocumentFormat.OpenXml.Packaging.WorksheetPart>();
                worksheetPart.Worksheet = new DocumentFormat.OpenXml.Spreadsheet.Worksheet(
                    new DocumentFormat.OpenXml.Spreadsheet.SheetData(
                        new DocumentFormat.OpenXml.Spreadsheet.Row(
                            new DocumentFormat.OpenXml.Spreadsheet.Cell
                            {
                                CellReference = "A1",
                                DataType = DocumentFormat.OpenXml.Spreadsheet.CellValues.InlineString,
                                InlineString = new DocumentFormat.OpenXml.Spreadsheet.InlineString(
                                    new DocumentFormat.OpenXml.Spreadsheet.Text("Gmail XLSX preview sentinel"))
                            })));
                workbookPart.Workbook.AppendChild(new DocumentFormat.OpenXml.Spreadsheet.Sheets(
                    new DocumentFormat.OpenXml.Spreadsheet.Sheet
                    {
                        Id = workbookPart.GetIdOfPart(worksheetPart),
                        SheetId = 1U,
                        Name = "Gmail"
                    }));
                worksheetPart.Worksheet.Save();
                workbookPart.Workbook.Save();
            }
            xlsxBytes = package.ToArray();
        }

        var messageJson = JsonSerializer.Serialize(new
        {
            id = "message-pdf-xlsx",
            threadId = "thread-pdf-xlsx",
            snippet = "PDF and workbook attachments",
            internalDate = "1790000000000",
            payload = new
            {
                headers = new[]
                {
                    new { name = "From", value = "sender@example.com" },
                    new { name = "Subject", value = "PDF and workbook" }
                },
                parts = new[]
                {
                    new
                    {
                        filename = "report.pdf",
                        mimeType = "application/pdf",
                        body = new { size = pdfBytes.Length, data = Base64Url(pdfBytes) }
                    },
                    new
                    {
                        filename = "report.xlsx",
                        mimeType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                        body = new { size = xlsxBytes.Length, data = Base64Url(xlsxBytes) }
                    }
                }
            }
        });

        var previousPreviews = Directory.EnumerateFiles(Path.GetTempPath(), "layal-gmail-preview-*")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var client = new HttpClient(new GmailInlineAttachmentHandler(messageJson, "message-pdf-xlsx"));
        var service = new GmailReadOnlyService(credentialPath, client);

        var detail = await service.ReadMessageAsync("message-pdf-xlsx");

        Assert.AreEqual(2, detail.Attachments!.Count);
        StringAssert.Contains(detail.Attachments[0].Status, "Local document text preview available");
        StringAssert.Contains(detail.Attachments[0].PreviewText, "Gmail PDF preview sentinel");
        StringAssert.Contains(detail.Attachments[1].Status, "Local document text preview available");
        StringAssert.Contains(detail.Attachments[1].PreviewText, "Gmail XLSX preview sentinel");
        Assert.AreEqual(0, Directory.EnumerateFiles(Path.GetTempPath(), "layal-gmail-preview-*")
            .Count(path => !previousPreviews.Contains(path)),
            "PDF and workbook temporary files must be removed immediately after local extraction.");
    }

    [TestMethod]
    public async Task ReadMessageAsyncOcrsSmallImageAttachmentsLocallyAndDeletesTemporaryFile()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("The Gmail credential store uses Windows DPAPI and OCR uses bundled Windows models.");

        using var temp = new TestTempDirectory();
        var credentialPath = Path.Combine(temp.Path, "gmail.dpapi");
        new ProtectedJsonStore<GmailOAuthCredential>(credentialPath).Save(new GmailOAuthCredential
        {
            ClientId = "123456789012-abcdefghijklmnop.apps.googleusercontent.com",
            RefreshToken = "test-refresh-token"
        });

        var fixturePath = Path.Combine(AppContext.BaseDirectory, "TestAssets", "arabic-ocr-fixture.png");
        var imageBytes = await File.ReadAllBytesAsync(fixturePath);
        Assert.IsTrue(imageBytes.Length <= GmailReadOnlyService.MaximumAttachmentBytes,
            "The OCR fixture must remain within Gmail's 1 MiB attachment preview limit.");
        var existingPreviews = Directory.EnumerateFiles(Path.GetTempPath(), "layal-gmail-preview-*")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var messageJson = JsonSerializer.Serialize(new
        {
            id = "message-image",
            threadId = "thread-image",
            snippet = "Image attachment",
            internalDate = "1790000000000",
            payload = new
            {
                headers = new[]
                {
                    new { name = "From", value = "sender@example.com" },
                    new { name = "Subject", value = "Image OCR preview" }
                },
                parts = new[]
                {
                    new
                    {
                        filename = "receipt.png",
                        mimeType = "image/png",
                        body = new { size = imageBytes.Length, data = Base64Url(imageBytes) }
                    }
                }
            }
        });

        using var http = new HttpClient(new GmailInlineAttachmentHandler(messageJson, "message-image"));
        var service = new GmailReadOnlyService(credentialPath, http);
        var detail = await service.ReadMessageAsync("message-image");

        Assert.AreEqual(1, detail.Attachments!.Count);
        StringAssert.Contains(detail.Attachments[0].Status, "Local image OCR preview available");
        StringAssert.Contains(detail.Attachments[0].PreviewText, "اختبار");
        Assert.IsTrue(detail.Attachments[0].PreviewText.Length <= GmailReadOnlyService.MaximumSingleAttachmentPreviewCharacters);
        var leakedPreviews = Directory.EnumerateFiles(Path.GetTempPath(), "layal-gmail-preview-*")
            .Where(path => !existingPreviews.Contains(path)).ToArray();
        Assert.AreEqual(0, leakedPreviews.Length,
            "Image attachment bytes may be processed in a private temporary file but the file must be removed immediately.");
    }

    private static string Base64Url(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');


    private sealed class GmailListStubHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path)> GmailRequests { get; } = new();
        public string ListRequestQuery { get; private set; } = string.Empty;
        public int MetadataRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI is missing.");
            if (uri.Host == "oauth2.googleapis.com" && uri.AbsolutePath == "/token")
            {
                Assert.AreEqual(HttpMethod.Post, request.Method);
                return Task.FromResult(JsonResponse("{\"access_token\":\"test-access-token\",\"expires_in\":3600}"));
            }

            if (uri.Host != "gmail.googleapis.com")
                throw new InvalidOperationException("Unexpected host in Gmail test: " + uri.Host);

            GmailRequests.Add((request.Method, uri.AbsolutePath));
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("Bearer", request.Headers.Authorization?.Scheme);
            Assert.AreEqual("test-access-token", request.Headers.Authorization?.Parameter);

            if (uri.AbsolutePath == "/gmail/v1/users/me/messages")
            {
                ListRequestQuery = uri.Query;
                return Task.FromResult(JsonResponse("{\"messages\":[{\"id\":\"message-1\"},{\"id\":\"message-2\"}]}"));
            }

            if (uri.AbsolutePath.EndsWith("/messages/message-1", StringComparison.Ordinal))
            {
                MetadataRequests++;
                return Task.FromResult(JsonResponse("{\"id\":\"message-1\",\"threadId\":\"thread-1\",\"snippet\":\"first snippet\",\"internalDate\":\"1790000000000\",\"payload\":{\"headers\":[{\"name\":\"From\",\"value\":\"sender@example.com\"},{\"name\":\"Subject\",\"value\":\"Subject one\"}]}}"));
            }

            if (uri.AbsolutePath.EndsWith("/messages/message-2", StringComparison.Ordinal))
            {
                MetadataRequests++;
                return Task.FromResult(JsonResponse("{\"id\":\"message-2\",\"threadId\":\"thread-2\",\"snippet\":\"second snippet\",\"payload\":{\"headers\":[{\"name\":\"From\",\"value\":\"other@example.com\"},{\"name\":\"Subject\",\"value\":\"Subject two\"}]}}"));
            }

            throw new InvalidOperationException("Unexpected Gmail API path: " + uri.AbsolutePath);
        }

        private static HttpResponseMessage JsonResponse(string content) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };
    }

    private sealed class GmailInlineAttachmentHandler(string messageJson, string messageId = "message-docx") : HttpMessageHandler
    {
        public List<string> RequestedPaths { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI is missing.");
            if (uri.Host == "gmail.googleapis.com")
                RequestedPaths.Add(uri.AbsolutePath);
            if (uri.Host == "oauth2.googleapis.com" && uri.AbsolutePath == "/token")
                return Task.FromResult(JsonResponse(JsonSerializer.Serialize(new
                {
                    access_token = "test-access-token",
                    expires_in = 3600
                })));

            if (uri.Host == "gmail.googleapis.com" &&
                uri.AbsolutePath.EndsWith("/messages/" + Uri.EscapeDataString(messageId), StringComparison.Ordinal))
                return Task.FromResult(JsonResponse(messageJson));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage JsonResponse(string content) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };
    }

    private sealed class GmailApiStubHandler(string fullMessageJson, string remoteAttachmentText) : HttpMessageHandler
    {
        public int AttachmentRequests { get; private set; }
        public List<string> RequestedPaths { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Missing request URI.");
            RequestedPaths.Add(uri.AbsolutePath);

            if (uri.AbsoluteUri == "https://oauth2.googleapis.com/token")
            {
                Assert.AreEqual(HttpMethod.Post, request.Method);
                return JsonResponse("{\"access_token\":\"test-access-token\",\"expires_in\":3600}");
            }

            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("Bearer", request.Headers.Authorization?.Scheme);
            Assert.AreEqual("test-access-token", request.Headers.Authorization?.Parameter);

            if (uri.AbsolutePath.EndsWith("/messages/message-1", StringComparison.Ordinal))
                return JsonResponse(fullMessageJson);

            if (uri.AbsolutePath.EndsWith("/attachments/attachment-1", StringComparison.Ordinal))
            {
                AttachmentRequests++;
                return JsonResponse(JsonSerializer.Serialize(new
                {
                    size = remoteAttachmentText.Length,
                    data = Base64Url(remoteAttachmentText)
                }));
            }

            throw new InvalidOperationException("The Gmail service made an unexpected network request: " + uri.AbsoluteUri);
        }

        private static Task<HttpResponseMessage> JsonResponse(string content) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            });
    }
}
