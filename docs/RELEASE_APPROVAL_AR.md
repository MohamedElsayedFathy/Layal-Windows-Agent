# بوابة اعتماد الإصدار والإعداد للنشر — Layal 0.5.2

**تاريخ آخر تحديث للسجل:** 27 أكتوبر 2026  
**الحالة:** **NO-GO — يمنع التوزيع العام/التجاري حاليًا**  
**الفرع الأساسي:** `main`  
**آخر خط أساس متكامل قبل تحديثات إجراءات CI:** `0473cf468540ae8400ae7ea830ce394815ad6ef6`  
**الدمج الأساسي:** [PR #10](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/pull/10)

## ملخص الأدلة الآلية

- نجح Windows CI لخط الأساس المذكور: Release build، واختبارات **83/83**، وفحوص النقر الشفاف والسحب، وفحص DPI أحادي الشاشة، ودورة حياة المثبّت، والتحقق من 10 ملفات في بصمات SHA-256.
- نجح فحص الأسرار وNuGet vulnerability audit على ذلك الخط الأساس؛ أظهر تدقيق NuGet صفر advisories.
- تمت مراجعة ودمج طلبات تحديث الإجراءات #14 (checkout v7)، #13 (setup-dotnet v6)، #12 (setup-python v6)، و#11 (upload-artifact v7)، بعد نجاح فحوص كل فرع. لا بد من مراجعة أحدث تشغيلين على `main` بعد هذه التحديثات وأي commit لاحق قبل ترشيح إصدار جديد.
- يمكن مراجعة تشغيلات `main` عبر [Windows build](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/workflows/windows-build.yml?query=branch%3Amain) و[Security/Supply Chain](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/workflows/security-supply-chain.yml?query=branch%3Amain).

## بوابات الإطلاق

| البوابة | الحالة | الدليل المطلوب للإغلاق |
|---|---|---|
| القبول اليدوي على Windows | **OPEN — سجل الأدلة PENDING** | [سجل القبول الحي](WINDOWS_LIVE_ACCEPTANCE_REPORT_AR.md) مكتمل بالأدلة الفعلية، و[سجل الأدلة القابل للتحقق آليًا](RELEASE_GATE_EVIDENCE.json) معتمد للـ commit المختبَر |
| Narrator/NVDA وHigh Contrast | **OPEN** | نتائج فعلية مسجلة على سطح مكتب Windows |
| RTL/LTR و100/125/150/175% | **OPEN** | مراجعة حية ولقطات من كل قيمة متاحة |
| شاشتان مختلفتا DPI | **OPEN/BLOCKED** | رصد انتقال فعلي للشاشة وDPI مع مراجعة بصرية |
| رخصة Layal نفسها | **OPEN — مانع نشر** | قرار موثق من أصحاب الحقوق وملف `LICENSE` متسق معه |
| حقوق الأصول | **OPEN — مانع نشر** | مصدر وترخيص وإذن إعادة توزيع لكل أصل، مسجلة ومراجعة |
| اعتماديات NuGet وSBOM | **OPEN — مانع نشر** | مراجع بشري لكل اعتماد، وحل بيانات الترخيص الناقصة وتوثيق الأدلة |
| توقيع Code Signing | **NOT CONFIGURED/NOT VERIFIED** | شهادة موثوقة وأسرار Actions وإثبات نجاح tag build مع Authenticode + RFC 3161 |
| حماية فرع main | **NOT ENABLED عند آخر قراءة API** | تشغيل أداة `Configure-LayalMainProtection.ps1 -Apply` بحساب Admin ثم التحقق عبر GitHub |
| tag وحزمة الإصدار المنشور | **NOT STARTED** | بعد الإغلاق فقط، tag على commit من `main` وحزمة موقعة وتحقق مستقل للبصمات |

## بوابة الحماية من النشر المبكر

يفرض مدقق التراخيص `--require-release-clearance` قرار رخصة المشروع وأدلة مراجعة الحزم والأصول وكمال حقول ترخيص SBOM عند تشغيل بناء الإصدار الموقّع. يقوم `Build-LayalDistribution.ps1 -RequireCodeSigning` بفحص قانوني تمهيدي **قبل نشر أو توقيع الملفات**، ثم يعيد التحقق مقابل SBOM الفعلي. لذلك فإن وجود نواقص حالية يجب أن يجعل مسار الإصدار الموقّع يفشل بدل إنشاء حزمة موقّعة قابلة للنشر رغم بقاء حقوق غير محسومة.

هذه آلية ضبط وليست موافقة قانونية؛ لا يمكن استبدال قرار صاحب الحقوق أو المراجع البشري بوضع حالة `REVIEWED` دون مستندات صحيحة.



## بوابة أدلة القبول اليدوي

أُضيفت بوابة مستقلة لـ Windows acceptance إلى tag workflow وإلى `Build-LayalDistribution.ps1 -RequireCodeSigning`. تطلب البوابة `docs/RELEASE_GATE_EVIDENCE.json` بأربع موافقات بشرية: قبول Windows والجلسات، Narrator/NVDA وHigh Contrast، RTL/LTR والتحجيم، وشاشتان مختلفتا DPI. كل بند يحتاج اسم مراجع وتاريخًا ومرجع دليل حقيقيًا.

حاليًا كل البنود `PENDING`، وهذا مقصود؛ لم تُسجّل هذه المراجعة اختبارات سطح مكتب فعلية. لذلك يجب أن يفشل tag release قبل إنشاء/توقيع الحزمة ما لم تُملأ الأدلة الصحيحة. يربط المدقق الأدلة بآخر commit اختُبر ويمنع أي تغييرات بعد الاختبار خارج ملف سجل الأدلة نفسه. راجع [إجراءات إغلاق البوابة](RELEASE_READINESS_GATE_AR.md).

## التوقيع الرقمي

استخدم فقط شهادة Code Signing موثوقة وصالحة. تحفظ أسرار GitHub Actions:
- `LAYAL_SIGNING_CERTIFICATE_BASE64`
- `LAYAL_SIGNING_CERTIFICATE_PASSWORD`
- `LAYAL_SIGNING_CERTIFICATE_THUMBPRINT`

لا تُرسل أسرار التوقيع في المستودع أو Issues أو المحادثة. يجب أن ينجح توقيع `Layal.exe` و`Layal.dll` والمثبّت، وختم RFC 3161، و`Verify-LayalDistributionIntegrity.ps1`، و`validate_distribution.py --artifacts --require-signed` على tag build قبل النشر.

## القرار النهائي الحالي

- Windows CI الأساسي: **PASS** على خط الأساس السابق.
- أحدث CI بعد تحديثات إجراءات GitHub: تحقق من نتيجة أحدث commit قبل الإصدار.
- قبول Windows الحي: **PENDING**.
- الرخصة والأصول: **OPEN**.
- التوقيع الموثوق: **NOT CONFIGURED/NOT VERIFIED**.
- حماية main: **لم تُثبت بعد**.
- القرار: **NO-GO — استخدام تطوير/اختبار داخلي فقط**.

انظر [خطة إغلاق القبول النهائي](FINAL_RELEASE_CLOSURE_AR.md) للتعليمات التنفيذية وسجل البنود المتبقية.
