# إغلاق قبول Windows وإعداد الإصدار — Layal 0.5.2

**القرار الحالي: NO-GO للتوزيع العام أو التجاري.**  
هذا السجل يميز العمل المنفذ في GitHub عن القبول الفعلي الذي لا يمكن إثباته من CI.

## ما تم تنفيذه في المستودع
- دمج التصميم والتوطين وإمكانية الوصول الأساسي في `main` عبر PR #10.
- مراجعة طلبات تحديث GitHub Actions #11–#14؛ نجحت فحوص البناء والأمان على كل head قبل الدمج، ثم دُمجت بالتتابع إلى `main`.
- تحديث إجراءات workflow الرئيسي إلى `actions/checkout@v7` و`actions/setup-dotnet@v6` و`actions/setup-python@v6` و`actions/upload-artifact@v7`.
- تحديث معرّفات Inno Setup إلى `x64compatible` لإزالة تحذير التقادم والسماح بتثبيت تطبيق x64 على Windows 11 ARM64 عبر المحاكاة عند الدعم.
- إضافة بوابة fail-closed تمنع بناء إصدار موقّع قبل تسجيل موافقة صريحة على رخصة المشروع، ومراجعة كل اعتماد، وإثبات مصدر وترخيص وإذن إعادة توزيع كل أصل، واكتمال بيانات SBOM. يتطلب سجل الأصل `sourceUrl` أو `sourceDescription`، و`licenseExpression` أو `license`، و`redistributionPermission`، و`reviewedBy`، و`reviewedOn`، و`evidence`؛ ويُرفض أي أصل مضاف إلى مجلد Assets ولم يُسجل في الجرد.
- إثراء حقول التراخيص الناقصة في CycloneDX من الجرد المعتمد مع حفظ روابط المصدر والترخيص وحقل يصرح بأن الإثراء لا يعد موافقة قانونية. تبقى الموافقات البشرية شرطًا مستقلًا.
- إضافة اختبارات Python للبوابة القانونية وأداة PowerShell لتطبيق إعدادات حماية `main` بعد تشغيلها بصلاحيات GitHub الإدارية.
- إضافة بوابة قبول يدوي fail-closed للإصدار الموقّع، مرتبطة بـ commit المختبَر وتسمح فقط بتغيير سجل الأدلة بعد الاختبار؛ تبقى بوابات الأدلة `PENDING` إلى أن ينفذها مراجع على Windows فعليًا.

## تحذيرات CI المتبقية
- تم تحديث إجراءات checkout/setup-dotnet/setup-python/upload-artifact إلى الإصدارات الرئيسية الحالية، واستبدال معرّفات Inno Setup القديمة `x64` بـ`x64compatible` وفق [التوثيق الرسمي لـInno Setup](https://jrsoftware.org/ishelp/topic_archidentifiers.htm).
- قد تظهر رسائل Node.js `punycode` و`url.parse()` داخل الحزمة التابعة لإجراء Gitleaks. الإجراء المثبت هو إصدار Node 24 الحالي؛ هذه رسائل deprecation من تبعيات الإجراء وليست تحذيرات من كود C# الخاص بليال. لا تُخفَ عبر `NODE_NO_WARNINGS`؛ تُراجع مجددًا عند تحديث upstream.

## ما لم يتم اعتماده بعد

| البوابة | الحالة | لماذا لا يجوز تسجيل PASS بعد؟ |
|---|---|---|
| قارئ الشاشة Narrator/NVDA | BLOCKED على هذا المسار | يتطلب سطح مكتب Windows تفاعليًا وفحص UI Automation الحقيقي |
| Windows High Contrast | BLOCKED | يلزم تمكين سمة تباين فعلية وفحص التباين والتركيز أثناء التشغيل |
| RTL/LTR | NOT TESTED يدويًا | تحتاج مراجعة بصرية فعلية في العربية والإنجليزية والنصوص المختلطة |
| DPI 100/125/150/175% | NOT TESTED يدويًا | لقطات WPF المولدة لا تغيّر إعدادات DPI الفعلية في Windows |
| شاشتان مختلفتا DPI | BLOCKED ما لم تتوفر الشاشة الثانية | يجب رصد انتقال MonitorDevice وWindowDpi أثناء سحب الشخصية فعليًا |
| رخصة المشروع | OPEN | لم يحدد صاحب الحقوق ترخيص المشروع. لا يختار النظام رخصة نيابة عنه |
| أصول الشخصية/المعاينة/الأيقونة | OPEN | سجل المصدر/الترخيص/إذن إعادة التوزيع غير مكتمل |
| مراجعة بشرية لاعتمادات NuGet وSBOM | OPEN | نجاح التدقيق الآلي لا يساوي موافقة قانونية؛ يلزم evidence لكل اعتماد |
| شهادة Code Signing موثوقة | NOT CONFIGURED/NOT VERIFIED | لم يُثبت وجود شهادة صحيحة وأسرار Actions الثلاثة، ولم ينجح tag build موقّع |
| حماية main على GitHub | NOT ENABLED عند آخر قراءة | أظهر GitHub أن `main` غير محمي. أداة الإعداد مرفقة لكن يلزم تشغيلها والتحقق من نتيجة API |

## تنفيذ القبول اليدوي على Windows

ابنِ/نزّل النسخة المرشحة وسجّل commit وSHA-256 في [سجل القبول الحي](WINDOWS_LIVE_ACCEPTANCE_REPORT_AR.md). لا تستخدم نسخة مثبتة قديمة.

### جمع أدلة التشخيص في جلسة واحدة

نزّل artifact التوزيع من تشغيل GitHub Actions المطابق، واحتفظ بملف portable ZIP وSHA256SUMS.txt في المجلد نفسه. تحقّق من أن checkout المصدر على نفس commit الذي أنتج الحزمة، ثم شغّل المشغّل من حساب Windows اختباري نظيف، لا يحتوي على مجلد بيانات Layal سابق ولا مفاتيح أو بيانات شخصية. لا تشغّله من GitHub Actions؛ سيحرّك المؤشر والنوافذ أثناء الاختبارات.

~~~powershell
git checkout <EXACT-TESTED-COMMIT-SHA>
$testedCommitSha = (git rev-parse HEAD).Trim()
$acceptanceArgs = @{
  PortableArchivePath = ".\artifacts\downloaded-distribution\Layal-0.5.4-portable-win-x64.zip"
  ChecksumManifestPath = ".\artifacts\downloaded-distribution\SHA256SUMS.txt"
  DeviceId = "lab-device-01"
  TestedCommitSha = $testedCommitSha
  ArtifactReference = "https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/<RUN-ID>"
  ConfirmDisposableProfile = $true
}
& ".\tools\Start-LayalLiveAcceptance.ps1" @acceptanceArgs
~~~

استبدل SHA واسم ZIP ورابط التشغيل بالقيم الفعلية. سيتحقق المشغّل من بصمة ZIP مقابل SHA256SUMS.txt، ثم يستخرج الملف التنفيذي من الحزمة نفسها ويشغّل اختبارات النقر والسحب والتنقل ومعاينة GLB ورصد الشاشة/DPI. عند توفر شاشتين مختلفتي المقياس، أضف RequireMixedDpiTransition = $true إلى جدول المعاملات. ينتج المشغّل live-acceptance-session.json وmanual-checklist.csv؛ تبقى هذه أدلة تشخيص وليست موافقات بشرية، وتظل بوابات القبول اليدوي PENDING حتى تُستكمل الخطوات التالية بأدلة فعلية.

### 1. قارئ الشاشة والتنقل
- افتح Narrator من Windows باستخدام `Win+Ctrl+Enter`، أو شغّل NVDA إن كان مثبتًا.
- استخدم Tab وShift+Tab وCtrl+1 إلى Ctrl+7 وF1؛ تحقق من قراءة أسماء التبويبات والحقول والأزرار والحالات وعدم فقدان التركيز.
- كرر بالعربية والإنجليزية وفي الثيمين الفاتح والداكن، وسجّل النتيجة الفعلية.

### 2. High Contrast
- من Settings → Accessibility → Contrast themes، فعّل سمة تباين Windows؛ لا تستخدم الثيم الداكن العادي بديلًا عنها.
- افحص التبويبات والحقول والأزرار والقوائم والتحديد ورسائل الخطأ والتركيز المرئي. أعد الإعداد السابق بعد الاختبار وسجل أي قص/ألوان ثابتة غير مقروءة.

### 3. RTL/LTR والتحجيم
- راجع الأقسام السبعة بالعربية ثم الإنجليزية: الرئيسية، المحادثة، المهام وسجلها، المهارات، الكاميرات، الذاكرة، والإعدادات.
- كرر بالفاتح والداكن، وجرّب URL ومعرّف النموذج والنصوص الطويلة/المختلطة الاتجاه.
- بدّل Display Scale بين 100% و125% و150% و175% عند توفر كل قيمة، ثم سجّل الصور والقص والتداخل.

### 4. شاشتان مختلفتا DPI
نفذ هذا الأمر من جذر المستودع على جهاز Windows يحتوي على شاشتين بمقياسين مختلفين، واسحب الشخصية بينهما خلال 90 ثانية:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\tools\Test-LayalDisplayDpi.ps1 -LayalExePath ".\src\Layal.Desktop\bin\Release\net10.0-windows\Layal.exe" -ObserveSeconds 90 -RequireMultipleMonitors -RequireMonitorTransition -RequireDpiTransition -LogDirectory "artifacts/display-dpi-manual"
```

تحقق من `TopologyDpiGateStatus=passed` و`TopologyDpiTransitionsObserved=true`، ثم افحص الصور والسلوك يدويًا. هذا التقرير لا يصادق وحده على جودة المظهر.

## إعداد شهادة التوقيع
لا تُنشئ شهادة ذاتية التوقيع بديلًا عن شهادة موثوقة للنشر العام. يجب أن يجهّز صاحب الإصدار شهادة Code Signing صالحة من جهة موثوقة، ثم يخزّن عبر GitHub Settings → Secrets and variables → Actions القيم:
- `LAYAL_SIGNING_CERTIFICATE_BASE64`
- `LAYAL_SIGNING_CERTIFICATE_PASSWORD`
- `LAYAL_SIGNING_CERTIFICATE_THUMBPRINT`

لا تضع PFX أو Base64 أو كلمة المرور في Git/Issue/المحادثة. بعد إغلاق الموافقات القانونية، أنشئ tag إصدار على commit موجود في `main` وشغّل workflow. يجب التحقق من Authenticode وRFC 3161 وسلامة البصمات في سجلات التشغيل قبل النشر.

## حماية main
افحص أولًا ثم طبّق فقط من جهاز موثوق عليه GitHub CLI مسجل بحساب لديه صلاحية Admin:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\tools\Configure-LayalMainProtection.ps1
pwsh -NoProfile -ExecutionPolicy Bypass -File .\tools\Configure-LayalMainProtection.ps1 -Apply
```

تتطلب الأداة كتابة اسم المستودع/الفرع للتأكيد قبل أي تعديل. ستطلب الموافقات البشرية، وحدود الدمج، ومنع force-push والحذف، وحماية من تجاوز المسؤول، وفحوص البناء والأمان المطلوبة. إذا كانت خطة GitHub لا تسمح بحماية مستودع خاص، يجب ترقية الخطة أو نقل المستودع إلى خطة تدعم الحماية؛ لا تعتبر نجاح تشغيل الأداة مفترضًا.

## سياسة الإغلاق
لا تُحوّل أي بند إلى PASS إلا مع اسم المنفذ والتاريخ وcommit والأدلة. لا تنشر إلا عندما تنجح فحوص CI للـcommit المرشح، وتغلق الموافقات القانونية، وتنجح شهادة التوقيع الفعلية والقبول اليدوي المطلوب.
