# بوابة اعتماد الإصدار والإعداد للنشر — Layal 0.5.8

**تاريخ آخر تحديث للسجل:** 17 نوفمبر 2026  
**الحالة:** **NO-GO — يمنع التوزيع العام/التجاري حاليًا**  
**الفرع الأساسي:** `main`  
**خط الأساس الحالي:** `f2e07ad463a3ceee5b30184afc4699ca03883e9` (دمج PR #199)

## ملخص الأدلة الآلية على خط الأساس الحالي

- نجح [Windows Build run 44134879589](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/44134879589) على commit `f2e07ad463a3ceee5b30184afc4699ca03883e9`. يتضمن التشغيل Release build والاختبارات الآلية وفحوص keyboard navigation وmouse/click-through وDPI smoke ودورة حياة المثبّت ورفع تقارير الأدلة.
- نجح [Security and supply-chain run 44134879577](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/44134879577) على commit نفسه.
- هذه نتائج CI بعد الدمج وليست قبولًا يدويًا. تظل أدلة Narrator/NVDA وWindows High Contrast وRTL/LTR والتحجيم متعدد الشاشات والميكروفون/السماعة مطلوبة على جهاز Windows فعلي.
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
| حماية فرع main | **NOT REVERIFIED على الخط الأساسي الحالي** | أعد قراءة branch protection/rulesets بصلاحيات مناسبة؛ لا يُستنتج التفعيل أو التعطيل من سجل أكتوبر القديم |
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

- Windows Build على commit الحالي `f2e07ad`: **PASS** — run [44134879589](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/44134879589).
- Security/Supply-chain على commit الحالي نفسه: **PASS** — run [44134879577](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/44134879577).
- قبول Windows الحي: **PENDING**.
- الرخصة والأصول: **OPEN**.
- التوقيع الموثوق: **NOT CONFIGURED/NOT VERIFIED**.
- حماية main: **لم تُثبت بعد**.
- القرار: **NO-GO — استخدام تطوير/اختبار داخلي فقط**.

انظر [خطة إغلاق القبول النهائي](FINAL_RELEASE_CLOSURE_AR.md) للتعليمات التنفيذية وسجل البنود المتبقية.
