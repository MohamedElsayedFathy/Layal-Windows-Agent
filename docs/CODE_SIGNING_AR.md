# التوقيع الرقمي والتحقق من سلامة ملفات Layal

## نطاق التوقيع

مسار الإصدار الموقّع يوقّع `Layal.exe` و`Layal.dll` قبل إنشاء ZIP، ثم يوقّع `Layal-Setup-<version>-win-x64.exe` بعد تجميع المثبّت. يستخدم SignTool خوارزمية SHA-256 وختمًا زمنيًا RFC 3161 من DigiCert. يتحقق البناء من توقيع Authenticode وسلسلة الثقة والطابع الزمني قبل كتابة حالة نجاح التوقيع. ويحتوي `Layal.dll` على تجميعة .NET الأساسية للتطبيق؛ لذلك يجري توقيعها والتحقق منها أيضًا.

لا تُوقّع مكتبات Runtime التابعة لـ Microsoft على أنها من ناشر Layal؛ التوقيع محصور في التطبيق والمُثبّت الصادرين عن المشروع.

## إعداد GitHub Actions Repository Secrets

أضف القيم التالية إلى GitHub Repository Settings → Secrets and variables → Actions. لا تضف ملفات PFX أو كلمات المرور إلى Git أو ملفات الإعداد:

| اسم السر | المحتوى |
|---|---|
| `LAYAL_SIGNING_CERTIFICATE_BASE64` | محتوى PFX المشفر بـ Base64، ويحتوي شهادة Code Signing والمفتاح الخاص |
| `LAYAL_SIGNING_CERTIFICATE_PASSWORD` | كلمة مرور PFX |
| `LAYAL_SIGNING_CERTIFICATE_THUMBPRINT` | بصمة الشهادة المتوقعة، 40 خانة hex |

مثال لاستخراج Base64 على جهاز إداري موثوق من PFX موجود محليًا دون إيداعه في المستودع:

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes("C:\\secure\\Layal-CodeSigning.pfx"))
```

قيمة Base64 تُعامل بنفس حساسية ملف PFX؛ فهي ليست تشفيرًا للمفتاح. استخدم شهادة صادرة عن جهة موثوقة، وذات Enhanced Key Usage الخاص بـ Code Signing (OID `1.3.6.1.5.5.7.3.3`)، وتحقق من صلاحيتها وسياسة التجديد والإلغاء لدى مزود الشهادة.

## قواعد التشغيل

- بناء PR/CI العادي: لا يمرر الـ workflow أسرار التوقيع إلى خطوة البناء، وتُوسم المخرجات صراحةً بأنها غير موقعة.
- tag يبدأ بـ `v`: يُشترط أن يشير إلى commit موجود بالفعل ضمن تاريخ `main`، ثم يطلب مسار التوقيع الأسرار الثلاثة. إذا كانت ناقصة أو كانت الشهادة غير مناسبة أو فشل Authenticode أو الختم الزمني، يتوقف الإصدار دون رفع artifact إصدار.
- يجب تقييد إنشاء tags من النمط `v*` على مسؤولي الإصدار يدويًا من خلال قواعد حماية المستودع؛ ملف workflow لا ينشئ هذه القواعد تلقائيًا.
- يُنشأ `SHA256SUMS.txt` بعد التوقيع، ويجب أن تغطي القائمة كل ملف داخل دليل التوزيع عدا ملف البصمات نفسه. يتحقق `Verify-LayalDistributionIntegrity.ps1` من كل بصمة وغياب الملفات غير المدرجة، ثم يفحص توقيع المُثبّت و`Layal.exe` و`Layal.dll` داخل ZIP.
- SHA-256 يكشف اختلاف المحتوى عن القائمة المقارَنة لكنه لا يثبت هوية المصدر بمفرده. تحقق من توقيعي Windows الموثوقين قبل اعتبار الملفات مخرجات إصدار معتمدة.

## تحقق يدوي على Windows

بعد تنزيل مُثبّت من قناة موثوقة:

```powershell
Get-AuthenticodeSignature .\\Layal-Setup-0.5.2-win-x64.exe | Format-List Status,StatusMessage,SignerCertificate,TimeStamperCertificate
Get-FileHash .\\Layal-Setup-0.5.2-win-x64.exe -Algorithm SHA256
```

يجب أن تكون `Status` هي `Valid` وأن تتطابق البصمة مع السطر المقابل في `SHA256SUMS.txt`. لا يكفي وجود اسم ناشر أو سلسلة نصية داخل الملف.

## الحالة الحالية

المستودع يضم مسار التوقيع والتحقق فقط؛ لم تُضبط شهادة فعلية في GitHub Actions Repository Secrets ولم يكتمل إصدار tag موقّع حتى الآن. لذلك لا تُعلن نسخة 0.5.2 الحالية موقعة، ولا تعتبر مرحلة التوقيع الفعلي مغلقة حتى يظهر تشغيل tag ناجح يتضمن توقيعًا صالحًا وختمًا زمنيًا وبصمات متحققة.


## Evidence from CI — 23 October 2026

- Windows CI run [39976840663](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/39976840663), commit `19fd097d5f3232158c70858c3a570ca54c6fa0c8`, completed successfully.
- `Verify-LayalDistributionIntegrity.ps1` verified SHA-256 entries for **10 distribution files**, checked that the installer and portable `Layal.exe` are explicitly unsigned on this non-release run, and returned `PASS`.
- Final distribution validation returned `Generated package validation: PASS` and `RESULT: PASS`. The [distribution artifact](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/39976840663/artifacts/12511818014) was uploaded.
- The tagged-release signing step was skipped because this was a branch/PR CI run, not a `v*` tag. The log explicitly reported `NOT SIGNED` because certificate secrets were not configured in this job. This confirms checksum/integrity verification for the unsigned build only; it does **not** prove a trusted Authenticode signature.
- A trusted certificate, configured repository secrets, and a successful tagged-release run remain mandatory to close the actual signing gate.
