using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Layal.Services;

/// <summary>
/// Centralizes URL, destination, page-target, upload, and search-result policy for the
/// dedicated Edge automation service. Kept separate from CDP process and transport lifecycle.
/// </summary>
internal static class BrowserAutomationPolicy
{
        internal static Uri ValidateHttpUrl(string value)
        {
            var text = (value ?? string.Empty).Trim();
            if (text.Length is 0 or > 4096 ||
                !Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                !string.IsNullOrEmpty(uri.UserInfo) ||
                uri.Host.Length == 0)
                throw new InvalidOperationException("Browser navigation only supports absolute HTTP/HTTPS URLs without embedded credentials.");
            return uri;
        }
    
        /// <summary>Fact-check source URLs are HTTPS-only, port 443, and never IP literals or local names.</summary>
        internal static Uri ValidateFactCheckSourceUrl(string value)
        {
            var uri = ValidateHttpUrl(value);
            var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
            if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                uri.Port != 443 || string.IsNullOrWhiteSpace(host) || uri.IsLoopback ||
                IPAddress.TryParse(host, out _) ||
                host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".test", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".example", StringComparison.OrdinalIgnoreCase) ||
                uri.AbsolutePath.Length > 2048 || uri.Query.Length > 2048)
                throw new InvalidOperationException("Fact-check sources must be public HTTPS URLs on the standard port, without local/IP hosts or reserved test domains.");
            return uri;
        }
    
        /// <summary>
        /// Conservative registrable-domain approximation for common public suffixes. It is used only
        /// to avoid treating subdomains of the same publisher as independent sources.
        /// </summary>
        internal static string GetRegistrableDomain(string host)
        {
            var normalized = (host ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
            if (normalized.Length == 0 || IPAddress.TryParse(normalized, out _)) return normalized;
            var labels = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (labels.Length <= 2) return normalized;
            var twoLabelSuffixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "ac.uk", "co.uk", "gov.uk", "org.uk", "sch.uk",
                "com.au", "net.au", "org.au", "edu.au", "gov.au",
                "co.nz", "org.nz", "govt.nz", "ac.nz",
                "co.jp", "ne.jp", "or.jp", "ac.jp", "go.jp",
                "com.br", "net.br", "org.br", "gov.br",
                "com.cn", "net.cn", "org.cn", "gov.cn",
                "com.sg", "com.my", "com.tr", "com.mx", "com.ar",
                "co.in", "firm.in", "net.in", "org.in", "gen.in", "ind.in",
                "com.eg", "gov.eg", "edu.eg", "org.eg", "net.eg"
            };
            var suffix = labels[^2] + "." + labels[^1];
            return string.Join(".", labels[^(twoLabelSuffixes.Contains(suffix) ? 3 : 2)..]);
        }
    
        internal static bool AreSameRegistrableDomain(string firstHost, string secondHost)
        {
            var first = GetRegistrableDomain(firstHost);
            var second = GetRegistrableDomain(secondHost);
            return first.Length > 0 && second.Length > 0 &&
                string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
        }
    
        internal static async Task ValidatePublicHostResolutionAsync(string host, CancellationToken token)
        {
            IPAddress[] addresses;
            try { addresses = await Dns.GetHostAddressesAsync(host, token); }
            catch (SocketException ex)
            {
                throw new InvalidOperationException("The approved source host could not be resolved to a public address.", ex);
            }
            if (addresses.Length == 0 || addresses.Any(address => !SafeBrowserDownloadService.IsPublicAddress(address)))
                throw new InvalidOperationException("The approved source host resolves to a private, local, reserved, or otherwise non-public IP address. No navigation was performed.");
        }
    
        internal static string ValidateCssSelector(string value)
        {
            var selector = (value ?? string.Empty).Trim();
            if (selector.Length is 0 or > 500 || selector.Any(char.IsControl))
                throw new InvalidOperationException("Enter a CSS selector of 1–500 printable characters.");
            return selector;
        }
    
        /// <summary>
        /// Rejects clicks that can directly submit/reset an HTML form or target an element inside or
        /// associated with a form. Allowed page click handlers can still have side effects.
        /// </summary>
        internal static void ValidateBrowserClickTarget(string tag, string type, string role, bool formAssociated)
        {
            var normalizedTag = (tag ?? string.Empty).Trim().ToLowerInvariant();
            var normalizedType = (type ?? string.Empty).Trim().ToLowerInvariant();
            var normalizedRole = (role ?? string.Empty).Trim().ToLowerInvariant();
    
            if ((normalizedTag is "button" or "input") && (normalizedType is "submit" or "reset"))
                throw new InvalidOperationException("Submit and reset controls are not supported by browser_click.");
    
            if (formAssociated)
                throw new InvalidOperationException("Controls inside or associated with an HTML form cannot be clicked by Layal. Fill fields if needed, then submit or reset the form manually in the browser.");
    
            var clickable = normalizedTag is "a" or "button" or "summary" ||
                normalizedRole == "button" ||
                (normalizedTag == "input" && normalizedType == "button");
            if (!clickable)
                throw new InvalidOperationException("Only a visible HTTP/HTTPS link, standalone button, role=button, input type=button, or summary can be clicked.");
        }
    
        /// <summary>Allows choosing a file only through one visible, enabled HTML file input.</summary>
        internal static void ValidateBrowserUploadTarget(string tag, string type)
        {
            if (!string.Equals((tag ?? string.Empty).Trim(), "input", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals((type ?? string.Empty).Trim(), "file", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("browser_upload_file requires one visible input[type=file] target. It never clicks or submits the form.");
        }
    
        /// <summary>Refuses ambiguous sessions instead of guessing which tab the user intended.</summary>
        internal static void ValidateSinglePageSession(int pageCount)
        {
            if (pageCount == 0)
                throw new InvalidOperationException("The dedicated Layal browser has no usable page tab. Navigate to an HTTP/HTTPS URL first.");
            if (pageCount != 1)
                throw new InvalidOperationException("Layal browser control is paused because the dedicated Edge window has multiple page tabs or popups. Close extra tabs/windows and retry; Layal will not guess which page you intended.");
        }
    
        /// <summary>
        /// Enforces the target page's accept attribute before a local file can be selected.
        /// A blank accept attribute means the page does not declare a type restriction.
        /// </summary>
        internal static void ValidateBrowserUploadAccept(string accept, string extension)
        {
            var ext = (extension ?? string.Empty).Trim().ToLowerInvariant();
            if (ext.Length < 2 || ext[0] != '.')
                throw new InvalidOperationException("The selected upload must have a recognized file extension.");
    
            var mime = ext switch
            {
                ".pdf" => "application/pdf",
                ".txt" => "text/plain",
                ".md" => "text/markdown",
                ".csv" => "text/csv",
                ".json" => "application/json",
                ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                _ => throw new InvalidOperationException("This file type is not supported for browser upload.")
            };
            var tokens = (accept ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => value.ToLowerInvariant())
                .ToArray();
            if (tokens.Length == 0 || tokens.Contains("*/*", StringComparer.Ordinal))
                return;
    
            var family = mime[..mime.IndexOf('/')];
            if (tokens.Any(token => token == ext || token == mime || token == family + "/*"))
                return;
    
            throw new InvalidOperationException(
                $"The selected file type '{ext}' does not match the website file input's accept restriction '{accept}'. No file was selected.");
        }
    
        /// <summary>
        /// Builds an HTTPS Bing search URL. Search text is normalized and bounded before it can be sent.
        /// </summary>
        internal static Uri BuildBingSearchUri(string query)
        {
            var rawQuery = query ?? string.Empty;
            if (rawQuery.Any(char.IsControl))
                throw new InvalidOperationException("The search query cannot contain control characters.");
            var normalized = string.Join(" ", rawQuery.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (normalized.Length is < 3 or > 500)
                throw new InvalidOperationException("The search query must contain 3–500 printable characters.");
    
            return new Uri("https://www.bing.com/search?q=" + Uri.EscapeDataString(normalized), UriKind.Absolute);
        }
    
        internal static bool IsBingDomain(Uri uri)
        {
            ArgumentNullException.ThrowIfNull(uri);
            return uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                (uri.Host.Equals("bing.com", StringComparison.OrdinalIgnoreCase) ||
                 uri.Host.EndsWith(".bing.com", StringComparison.OrdinalIgnoreCase)) &&
                string.IsNullOrEmpty(uri.UserInfo);
        }
    
        /// <summary>
        /// Classifies common Bing challenge, consent, no-results, and unavailable states without
        /// treating arbitrary redirected sites as a successful search.
        /// </summary>
        internal static string ClassifyBingSearchPage(string finalUrl, string title, string visibleText)
        {
            if (!Uri.TryCreate(finalUrl, UriKind.Absolute, out var uri) || !IsBingDomain(uri))
                return "unexpected_redirect";
    
            var content = (title ?? string.Empty) + "\n" + (visibleText ?? string.Empty);
            var blockedPhrases = new[]
            {
                "access denied", "your request has been blocked", "temporarily blocked",
                "unusual traffic", "automated queries", "verify you are not a robot",
                "verify you're not a robot", "verify that you are human", "verify you're human",
                "solve the following challenge", "complete the security check", "captcha", "too many requests"
            };
            if (blockedPhrases.Any(phrase => content.Contains(phrase, StringComparison.OrdinalIgnoreCase)))
                return "blocked_or_challenge";
    
            var consentPhrases = new[]
            {
                "consent required", "before you continue", "choose your privacy settings", "privacy choices"
            };
            if (consentPhrases.Any(phrase => content.Contains(phrase, StringComparison.OrdinalIgnoreCase)))
                return "consent_required";
    
            var noResultsPhrases = new[]
            {
                "there are no results for", "no results found",
                "your search did not match any documents", "we didn't find results for"
            };
            if (noResultsPhrases.Any(phrase => content.Contains(phrase, StringComparison.OrdinalIgnoreCase)))
                return "no_results";
    
            if (string.IsNullOrWhiteSpace(visibleText))
                return "empty_page";
    
            return "results_available";
        }
}
