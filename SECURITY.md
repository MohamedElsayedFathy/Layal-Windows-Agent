# Security Policy

## Supported versions

Layal is currently a development-stage Windows application. At the time this policy was added, version **0.5.2** is a development milestone; the project has not declared a supported stable release series.

| Version / branch | Security-fix policy |
| --- | --- |
| Latest commit on `main` | Security reports are triaged and fixes are considered for the current development line. |
| Latest stable release, once one is declared | The preferred release line for security fixes. |
| Older releases, previews, forks, and unsigned CI artifacts | Reviewed case by case; please include the exact version or commit. |

Please use the latest available revision when investigating a report. A fix on `main` does not by itself mean an older build has been patched or that a release has been published.

## Reporting a vulnerability privately

**Please do not publish vulnerability details, exploit code, secrets, personal data, or a proof of concept in a public issue, discussion, pull request, or commit.**

### Preferred route: GitHub private vulnerability reporting

1. Open the repository's [Security Advisories page](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/security/advisories).
2. If the **Report a vulnerability** button is available, use it to submit a private report to the maintainers.
3. Include the information requested below, keeping the proof of concept minimal and non-destructive.

Private vulnerability reporting is a repository setting and may not be enabled. This policy does **not** claim that the feature is currently enabled.

### If private reporting is unavailable

Open a public issue titled **“Request private security contact for a vulnerability report”** at the [new issue page](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/issues/new). Include only that you have a security report and a safe way for the maintainers to follow up. **Do not include the vulnerability details, affected secret values, logs, payloads, screenshots, or exploit steps in that issue.** The maintainers will use the issue to arrange a private channel or initiate a private GitHub Security Advisory. If a private route cannot be arranged, do not post sensitive technical details publicly.

This fallback is a request for a secure contact, not a claim that a private reporting channel has already been configured.

## What to include

When a private channel is available, please provide as much of the following as you safely can:

- A concise description of the potential vulnerability and its security impact.
- Affected version, commit SHA, Windows version, and relevant configuration.
- Minimal steps to reproduce the issue in an environment you own or are authorized to test.
- A minimal proof of concept, logs, or screenshots only when needed and with secrets, tokens, personal information, and unrelated data removed.
- Any practical mitigation you have identified.

Do not send live credentials, signing certificates/private keys, full browser profiles, private user data, or data obtained from systems without authorization. Do not attempt destructive actions, persistence, lateral movement, or access to other people's data as part of testing.

## Scope

Reports are relevant when they demonstrate a security impact in Layal itself, including:

- The Windows application, installer, packaging/release scripts, or GitHub Actions workflows.
- Workspace file handling, path validation, skill ZIP import/export, or data-integrity boundaries.
- Tool invocation, approval enforcement, local process execution, or Windows UI automation.
- Browser automation, the dedicated Edge profile, or the local DevTools/CDP endpoint lifecycle.
- Handling and storage of API credentials, chat/session data, memory, task history, and logs.
- Application API integrations and trust boundaries between tool output and model input.

Please describe the impact on Layal rather than only reporting a vulnerability in an unrelated third-party service or dependency. Ordinary bugs, feature requests, and missing functionality without a demonstrable security impact should be filed through normal public project channels instead.

## Response targets

These are response targets, not guarantees:

- **Acknowledgement:** within 5 business days after the report reaches a usable channel.
- **Initial triage:** within 10 business days, including a preliminary assessment or a request for essential information.
- **Status updates:** at least every 15 business days while the report remains active, when practicable.
- **Fix and disclosure timing:** depends on severity, reproducibility, affected versions, and release readiness. The maintainers will try to coordinate a fix and disclosure with the reporter.

A report may take longer if it lacks enough detail to reproduce or if additional investigation is required. No bug bounty or financial compensation is promised by this policy.

## Coordinated disclosure

Please give the maintainers a reasonable opportunity to investigate and prepare a mitigation before publicly disclosing the issue. We will aim to keep the reporter informed and coordinate publication when a fix or mitigation is available. If there is evidence of active exploitation or a risk to users, mention that urgency in the private report.

---

## ملخص بالعربية

ليال ما زالت في مرحلة التطوير، والإصدار 0.5.2 ليس إصدارًا مستقرًا معلنًا. نرجو عدم نشر تفاصيل الثغرات أو خطوات استغلالها أو الأسرار في البلاغات العامة.

- استخدم صفحة [Security Advisories](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/security/advisories) وزر **Report a vulnerability** إذا كان متاحًا.
- ميزة الإبلاغ الخاص إعداد مستقل في GitHub، ولم يتم تأكيد أنها مفعّلة لهذا المستودع.
- إذا لم يظهر الزر، افتح [بلاغًا عامًا لطلب وسيلة تواصل آمنة فقط](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/issues/new)، دون تفاصيل تقنية أو ملفات أو أسرار؛ سيُستخدم البلاغ لترتيب قناة خاصة.
- الهدف هو تأكيد الاستلام خلال 5 أيام عمل، والفرز الأولي خلال 10 أيام عمل، وتحديث الحالة كل 15 يوم عمل على الأقل ما أمكن. هذه أهداف وليست ضمانًا زمنيًا.
- اختبر فقط الأنظمة والبيانات التي تملكها أو لديك إذن صريح لاختبارها، واحذف الأسرار والبيانات الشخصية من الأدلة.
