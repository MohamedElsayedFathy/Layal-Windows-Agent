# بوابة اعتماد الإصدار والإعداد للنشر — Layal 0.5.2

**تاريخ المراجعة:** 23 أكتوبر 2026  
**النسخة المرشحة:** 0.5.2 (رقم نسخة المصدر الحالي، وليس إصدارًا عامًا منشورًا)  
**القرار الحالي:** **NO-GO — لا تنشر الآن**  
**الفرع:** \`feature/layal-animated-task-props\`  
**Commit المراجع:** \`c5138a4ccae9b4b66215be03b8cd2bc4e1cce7ea\`  
**PR:** [#6 — Draft وغير مدمج](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/pull/6)

هذا السجل يفصل ما نجح في CI عمّا يجب أن يسبق إصدارًا عامًا. نجاح CI لا يمنح موافقة قانونية ولا يثبت قبول التطبيق على جهاز المستخدم. لم تُنشأ Git tag أو GitHub Release في هذه المرحلة.

## 1. أدلة CI الحالية

| البوابة | الدليل | النتيجة | حدود الإثبات |
|---|---|---|---|
| Windows Release build والحزمة | [Windows build run 39983593458](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/39983593458) | PASS | تشغيل PR؛ ناتج التوزيع غير موقّع |
| اختبارات C# | التشغيل نفسه | 77 ناجحًا، 0 فشل، 0 متخطّى | اجتياز الاختبارات الآلية فقط |
| اختبار النقر الشفاف | التشغيل نفسه | 4/4 PASS | عامل Windows CI |
| السحب وحفظ/استعادة الموضع | التشغيل نفسه | 6/6 PASS | عامل Windows CI |
| فحص الشاشة وDPI | التشغيل نفسه | 11 تحققًا آليًا PASS | شاشة واحدة فقط عند 96 DPI/100%؛ انتقال الشاشات غير معتمد، والمراجعة البصرية اليدوية pending |
| دورة حياة المثبّت | التشغيل نفسه | تثبيت/ترقية/تراجع/إزالة PASS، مع بقاء بيانات الاختبار | إصدار التراجع 0.5.1 fixture اصطناعي من المصدر الحالي، لا مُثبّت تاريخي حقيقي |
| سلامة الحزمة | التشغيل نفسه | تحقق من 10 بصمات SHA-256؛ والتحقق النهائي PASS | البصمات لا تثبت هوية الناشر من دون قناة ثقة |
| NuGet vulnerability audit | [Security run 39983593523](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/39983593523) | PASS | تدقيق آلي للشجرة التي جرى فحصها |
| فحص الأسرار Gitleaks | التشغيل الأمني نفسه | PASS | يغطي نطاق/تاريخ Git المتاح لعملية الفحص |
| جرد التراخيص/SBOM | تشغيل البناء الأمني | PASS WITH OPEN LEGAL GATES | جرد المكونات لا يساوي موافقة قانونية، وتوجد مكونات يتطلب حقل الترخيص فيها تحققًا من المصدر |

## 2. حزمة مرجعية للتجربة — ليست للنشر العام

- [حزمة التوزيع في CI](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/39983593458/artifacts/12514233645) — artifact ID: \`12514233645\`، الحجم قرابة 130.5 MB.
- [حزمة التشغيل الذاتي](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/39983593458/artifacts/12515405985).
- [تقارير الاختبارات](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/39983593458/artifacts/12513859877).
- [أدلة التفاعل الحي داخل CI](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/39983593458/artifacts/12514492240).
- [أدلة العرض/DPI](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/39983593458/artifacts/12514123413).
- [تقرير التدقيق الأمني](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/39983593523/artifacts/12514791607).

هذه artifacts خاصة بتشغيل CI، وصلاحيتها محدودة بفترة الاحتفاظ التي يضبطها workflow (14 يومًا للمخرجات المذكورة). نزّلها واحفظها في مخزن إصدار موثوق إذا كانت مطلوبة كسجل داخلي، لكن لا تقدّمها على أنها حزمة إصدار موقعة أو مقبولة يدويًا.

## 3. بوابات مانعة للنشر العام

| ID | الشرط | الحالة الحالية | دليل الإغلاق المطلوب |
|---|---|---|---|
| REL-01 | قبول يدوي على Windows فعلي | **OPEN** | تعبئة [سجل القبول الحي](WINDOWS_LIVE_ACCEPTANCE_REPORT_AR.md) مع نتائج وصور وسجلات من الجهاز المستهدف |
| REL-02 | قبول الشاشات وDPI | **OPEN** | تجربة مقاييس Windows الفعلية؛ وتجربة شاشة ثانية مختلفة المقياس عندما تكون ضمن بيئة الهدف |
| REL-03 | توقيع موثوق للتطبيق والمُثبّت | **OPEN** | إعداد شهادة Code Signing موثوقة وأسرار GitHub Actions ثم نجاح tag build الموقّع مع RFC 3161 timestamp والتحقق من Layal.exe وLayal.dll والمثبت |
| REL-04 | رخصة المشروع نفسه | **OPEN** | قرار صريح من مالك المشروع وإضافة ملف الرخصة الموافق؛ لا تختَر رخصة نيابةً عن المالك |
| REL-05 | حقوق أصول الشخصية/الأيقونات/الصور | **OPEN** | إثبات المصدر والترخيص وحق إعادة التوزيع، ومراجعة قانونية بشرية |
| REL-06 | مراجعة التراخيص وSBOM النهائية | **OPEN** | مراجعة بشرية لجرد CycloneDX والرخص المباشرة والمتعدية وإشعارات الأصول، ومعالجة الحقول غير المحددة أو توثيقها |
| REL-07 | قبول المتطلبات وحدود المنتج | **OPEN** | تحديث سجل المتطلبات الرئيسي وتوثيق الميزات غير المنفذة/غير المتحقق منها في بيان الإصدار |
| REL-08 | إدخال التغييرات إلى main بصورة معتمدة | **BLOCKED** | مراجعة PR #6 وإغلاق كل البوابات قبل الدمج؛ PR الحالي ما زال Draft وغير مدمج |
| REL-09 | إنشاء tag وتشغيل مسار الإصدار | **NOT STARTED** | بعد اعتماد كل الشروط أعلاه، يجب أن يكون tag مثل \`v0.5.2\` موجّهًا إلى commit موجود بالفعل في تاريخ \`main\` وأن ينجح مسار توقيع/تحقق التوزيع |
| REL-10 | نشر صفحة إصدار وملفاتها | **NOT STARTED** | رفع الملفات الناتجة من تشغيل tag موقّع إلى GitHub Release مع ملاحظات الإصدار والبصمات وتقرير الحالة وSBOM وإشعارات الطرف الثالث |

## 4. إجراء النشر المسموح بعد إغلاق البوابات

1. راجع [سجل المتطلبات الرئيسي](MASTER_REQUIREMENT_REGISTER_AR.md) و[سجل القبول الحي](WINDOWS_LIVE_ACCEPTANCE_REPORT_AR.md) واحفظ أدلتهما.
2. يراجع مالك المشروع الترخيص وحقوق الأصول ويعتمد قرار الإصدار. لا تُخزن شهادة التوقيع أو كلمة مرورها في Git أو ملفات الدليل.
3. أكمل مراجعة PR #6 ثم ادمج فقط بعد اجتياز شروط الاعتماد. لا تنشئ tag على الفرع الحالي قبل دخوله إلى \`main\`.
4. جهّز أسرار Actions التالية في إعدادات GitHub المحمية: \`LAYAL_SIGNING_CERTIFICATE_BASE64\` و\`LAYAL_SIGNING_CERTIFICATE_PASSWORD\` و\`LAYAL_SIGNING_CERTIFICATE_THUMBPRINT\`. لا ترسل هذه القيم في المحادثة.
5. بعد اعتماد commit على \`main\)، أنشئ tag الإصدار الموافق لقرار المالك، مثل \`v0.5.2\`. يجب أن يرفض workflow الحزمة إذا غابت شهادة التوقيع أو فشل ختم RFC 3161 أو تحقق Authenticode أو البصمات.
6. افحص ناتج tag workflow: التواقيع الفعلية، الطابع الزمني، تغطية SHA256SUMS، سلامة ZIP، SBOM، قائمة الإشعارات، وملف حالة الإصدار. لا تعِد استخدام artifact غير الموقّع الخاص بتشغيل PR.
7. أنشئ صفحة إصدار GitHub وارفع **مخرجات tag workflow نفسها** مع ملف البصمات وملاحظات الإصدار. ثم تحقق من تنزيلها وإعادة حساب SHA-256 من نسخة مستقلة.
8. بعد النشر، احتفظ بمعلومات commit/tag وتقرير التحقق ومراجعات القبول في أرشيف الإصدار.

## 5. قرار الاعتماد الحالي

- **Build/Test CI:** PASS.
- **Security automation:** PASS.
- **Package integrity (unsigned CI package):** PASS.
- **Live target-Windows acceptance:** PENDING.
- **Trusted code signing:** NOT CONFIGURED / NOT VERIFIED.
- **Project and asset legal review:** OPEN.
- **Approved main commit / signed release tag / published GitHub Release:** NOT STARTED.
- **Final decision:** **NO-GO — استمر في الاختبار الداخلي فقط.**

الموافق التقني: TBD  
مراجع الترخيص والأصول: TBD  
تاريخ اعتماد مالك المشروع: TBD

**لا تُحوّل القرار إلى GO اعتمادًا على نجاح CI وحده أو على كون المثبّت قد تم إنشاؤه بنجاح.**
