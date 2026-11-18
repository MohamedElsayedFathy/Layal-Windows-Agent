# هوية نموذج Qwen ودورة حياته — Phase C

## النطاق المثبّت

- الاسم الذي يطلبه التطبيق: `qwen3.5:2b`.
- Registry: `https://registry.ollama.ai`.
- مسار manifest: `/v2/library/qwen3.5/manifests/2b`.
- لا توجد في هذا الدليل بصمة SHA-256 ثابتة للـ tag؛ فالـ tag قابل للتغيير، والبصمة تُستخرج وقت التحقق وتُحفظ في pin محلي بعد مطابقة النموذج المثبّت بها.

## آلية التحقق المنفذة على فرع Phase C

1. يُطلب manifest من HTTPS على المضيف الثابت أعلاه، مع تعطيل اتباع التحويلات، وتحديد حد أقصى لحجم الرد.
2. إذا طلب Registry مصادقة Bearer، يقرأ العميل challenge. يجب أن يكون `realm` عبر HTTPS وعلى نفس سلطة Registry تمامًا. يُوقّع طلب token بمفتاح Ed25519 المحلي الذي أنشأه Ollama (`%USERPROFILE%\\.ollama\\id_ed25519`). لا تُرسل بايتات المفتاح الخاص؛ يُرسل فقط رأس المصادقة الذي يحتوي على المفتاح العام المرمّز والتوقيع.
3. يحسب العميل SHA-256 من البايتات الخام للـ manifest. إذا أعاد Registry رأس `Docker-Content-Digest`، يجب أن يطابق القيمة المحسوبة.
4. إذا كان الرد manifest عاديًا، تكون بصمة manifest هي البصمة المقبولة من `/api/tags`. وإذا كان الرد manifest list / OCI index، تُقرأ بصمات manifests الأبناء ذات نوع المحتوى المدعوم. ويُسمح أيضًا ببصمة manifest الأم لتوافق إصدارات Ollama التي قد تعرضها في `/api/tags`. كل قيمة مقبولة يجب أن تكون SHA-256 كاملة وموجودة حرفيًا داخل الرد الذي تم التحقق من بصمته.
5. بعد التنزيل أو عند ترقية pin قديم، يجب أن تطابق البصمة التي يعرضها Ollama محليًا إحدى القيم المقبولة من Registry. إذا لم تطابق، يفشل التحقق ولا يُنشأ pin جديد.
6. لا يصبح pin موثوقًا في هذا التطبيق إلا بعد نجاح مطابقة Registry واجتياز health check المحلي. يحفظ pin: اسم النموذج، بصمة النموذج المحلية، مضيف ومسار Registry، نوع manifest، حجم رد manifest، وقت التحقق، بصمة manifest الأم، والبصمات المقبولة من داخله.
7. عند التشغيل اللاحق، يمكن التحقق دون اتصال بمقارنة digest المحلي بالـ pin المحفوظ. لا يُتبع tag متغير تلقائيًا في كل تشغيل ولا يُحدّث pin بصمت.

## المصادقة وحدود الثقة

- خدمة Ollama المحلية مسؤولة عن إنشاء وإدارة مفتاح Ed25519 في مجلد المستخدم. إذا كان المفتاح غير موجود أو غير صالح، يفشل طلب Registry المغلق افتراضيًا؛ لا ينشئ Layal مفتاحًا بديلًا ولا يرسل المفتاح الخاص.
- تُرفض التحويلات إلى مضيف آخر، وكذلك token realm الذي لا يستخدم HTTPS أو لا يطابق سلطة Registry الأصلية.
- يعتمد هذا التحقق على اتصال TLS وثقة نظام التشغيل بمُصدِر الشهادة وباستجابة Registry الرسمية. وهو يثبت أن digest المحلي يطابق manifest الذي أعاده Registry في تلك اللحظة؛ **ولا يمثل توقيعًا منفصلًا من ناشر Qwen أو إثباتًا تشفيريًا مستقلًا لهوية مؤلف الأوزان**.
- المصادقة والتوكن مؤقتان داخل الذاكرة ولا يُحفظ bearer token ضمن pin.

## سلوك الفشل الحالي

- اختلاف digest بين Ollama وRegistry: رفض التحقق.
- Registry غير متاح أثناء أول تنزيل أو ترقية pin قديم: لا يتم اعتماد نموذج جديد اعتمادًا على TOFU فقط.
- وجود pin سابق مع غياب النموذج المحلي: يتوقف الإعداد بدل تنزيل أحدث محتوى للـ tag المتغير تلقائيًا. يوجد الآن مسار تحديث صريح منفصل؛ لا يبدأ إلا بعد موافقة المستخدم ووجود Registry-backed pin صالح.
- pin موجود لكن digest المحلي تغير: يُرفض النموذج ولا يُستبدل pin تلقائيًا.
- لم تُضف بعد اختيارات variants أو تحديد GPU/VRAM آليًا؛ ذلك مؤجل لحين جمع benchmarks قابلة للتكرار على أجهزة مستهدفة.
- لا يمنح هذا المسار Qwen صلاحية تنفيذ المهام. يبقى تخطيط/تنفيذ الأوامر عبر `NativeLanguageParser` و`NativeAgentOrchestrator` والموافقات والأدوات المسموح بها.

## التحديث والرجوع الآمن

- يظهر زر مستقل بعنوان «تحديث نموذج Qwen بأمان»؛ ولا يتبع التطبيق tag المتغير تلقائيًا عند التشغيل العادي.
- قبل التغيير، يطلب التطبيق manifest الحالي من Registry ويقارنه ببصمة manifest المسجلة في الـ pin. إذا لم تتغير البصمة فلا يبدأ تنزيلًا جديدًا.
- إذا تغيرت، ينشئ التطبيق نسخة Ollama محلية ثانية باستخدام `ollama cp` باسم رجوع مستقل، ثم يحفظ سجل تعافٍ ذريًا يتضمن alias الرجوع والـ pin السابق قبل استبدال tag النموذج.
- يبدأ `/api/pull` بعد إنشاء نسخة الرجوع فقط. ولا يُعتمد pin الجديد إلا بعد أن يكون digest المثبت ضمن القيم التي يعلنها manifest الحالي، ثم ينجح اختبار محادثة محلي.
- عند فشل التنزيل، أو إلغاء المستخدم، أو اختلاف digest، أو فشل health check أو حفظ pin، يعيد التطبيق alias السابق إلى `qwen3.5:2b` ويعيد pin السابق، ثم يتحقق من digest القديم ويعيد اختبار المحادثة.
- إذا تعذر إثبات الرجوع، يحتفظ التطبيق بسجل التعافي ونسخة الرجوع ولا يحذفهما. وعند تشغيل الإعداد أو التحقق من الموارد في المرة التالية، يحاول استعادة النموذج السابق وإثبات digest وصحة المحادثة قبل حذف السجل.
- لا يُحذف alias الرجوع إلا بعد حذف سجل التعافي بنجاح. ويُحتفظ به عند تعذر حذف السجل.
- مساحة التخزين المطلوبة للتحديث تُحسب بتحفظ على أساس نسختين من الحد الأعلى لحجم النموذج مع هامش إضافي؛ ويعرض التطبيق موافقة مسبقة على احتياج المساحة.
- هذا المسار لا ينفذ تحديثًا تلقائيًا في الخلفية، ولا يمنح Qwen صلاحية تنفيذ المهام.

## مصفوفة التحقق

| الاختبار | الحالة |
|---|---|
| manifest عادي: حساب SHA-256 من bytes ومطابقة `Docker-Content-Digest` عند وجوده | نجح ضمن Windows CI على الرأس `b5e93dd2be6e0b240ef0d205022cbec6c7406a22` |
| manifest list: استخراج بصمات الأبناء وبصمة الأم | نجح ضمن Windows CI على الرأس `b5e93dd2be6e0b240ef0d205022cbec6c7406a22` |
| challenge Bearer ثم إعادة طلب manifest بالتوكن | اختبار الـ fixture السابق نجح بمزوّد اصطناعي؛ أضيف الآن اختبار حي يلتقط 401 الفعلي ويرسل التوقيع ثم يطلب manifest مرة ثانية؛ نتيجة Windows CI الجديدة قيد التنفيذ |
| اختبار صيغة مفتاح Ollama الفعلية | أضيف إنشاء مفتاح OpenSSH-format داخل الاختبار للتحقق من قارئ SSH.NET؛ وأضيف تشغيل الاختبار بمفتاح Windows الذي ينشئه Ollama في workflow المعزول؛ النتيجة الحية قيد التنفيذ |
| رفض اختلاف رأس `Docker-Content-Digest` عن البايتات | نجح ضمن Windows CI على الرأس `b5e93dd2be6e0b240ef0d205022cbec6c7406a22` |
| رفض التحويلات والأنواع غير المدعومة والردود الكبيرة | نجح ضمن Windows CI على الرأس `b5e93dd2be6e0b240ef0d205022cbec6c7406a22` |
| انتهاء مهلة Registry أثناء قراءة body بطيئة/معلّقة | نجح اختبار regression بإعداد مهلة قصيرة على الرأس `b5e93dd2be6e0b240ef0d205022cbec6c7406a22` |
| Windows build واختبارات C# وحزمة التوزيع واختبار دورة حياة المثبّت | نجحت: run 44310907405 |
| Full Windows PowerShell acceptance ورفع الأدلة | نجح: run 44310907333 |
| NuGet advisory/license audit وfull-history Gitleaks | نجح: run 44310907496 |
| توقيع Ed25519 من ملف Ollama حقيقي وقبول token على Registry الحي | أُضيف في Workflow Windows منفصل؛ التشغيل الحالي قيد التنفيذ، ولا يُسجل PASS قبل نجاحه |
| تنزيل حقيقي على Windows، الإلغاء والاستئناف، digest محلي، وhealth check | أُضيف اختبار على خادم Ollama بمجلد Models مؤقت؛ التنزيل والقياس ما زالا قيد التشغيل ولم تُسجل نتيجة PASS بعد |
| update/rollback على نسخة Models محلية تجريبية معزولة | أُضيف مسار تحديث آمن داخل التطبيق واختبارات transaction، واختبار Windows حي لـ `ollama cp/rm` والاسترجاع؛ النتائج الجديدة قيد التنفيذ |
| قياس الحجم الفعلي المنزّل ومواءمته مع المساحة المحجوزة | Workflow الجديد يكتب مجموع أحجام ملفات blobs الفعلية في evidence؛ القياس النهائي لم يصدر بعد |
| قياس RAM/VRAM/سرعة inference ومقارنة variants | مؤجل؛ لا يوجد benchmark قابل للتكرار بعد |

## الحالة الحالية

الكود واختبارات الوحدة ومسار التحديث/الرجوع موجودة على `feature/phase-c-qwen-registry-digest`، ولم تُدمج في `main` بعد. الاختبارات الجديدة التي تتحقق من صيغة OpenSSH، والتبادل الحي مع Registry، ودورة التنزيل والإلغاء/الاستئناف على Windows، ما زالت قيد التنفيذ في GitHub Actions على هذا الفرع. لذلك لا يوجد حتى الآن digest فعلي معتمد أو حجم تنزيل مقاس أو نتيجة health check حي مسجلة. لا تُعلن Phase C مكتملة قبل نجاح هذه الـ workflows ومراجعة evidence الذي تنتجه.
## مراجع التنفيذ الرسمية

- Ollama Registry challenge, token request, and signed authorization: [`server/auth.go`](https://github.com/ollama/ollama/blob/main/server/auth.go) and [`auth/auth.go`](https://github.com/ollama/ollama/blob/main/auth/auth.go).
- Ollama manifest media types and child-manifest digest calculation: [`manifest/manifest.go`](https://github.com/ollama/ollama/blob/main/manifest/manifest.go).
- Ollama `/api/tags` selected-child behavior for manifest lists: [`server/model_list.go`](https://github.com/ollama/ollama/blob/main/server/model_list.go).
- SSH.NET package version and upstream license: [NuGet SSH.NET 2026.0.1](https://www.nuget.org/packages/SSH.NET/2026.0.1) and [upstream LICENSE](https://github.com/sshnet/SSH.NET/blob/develop/LICENSE).
- SSH.NET's transitive BouncyCastle package and its MIT / modified-Bzip2 Apache 2.0 note: [NuGet BouncyCastle.Cryptography 2.7.0](https://www.nuget.org/packages/BouncyCastle.Cryptography/2.7.0) and [upstream README](https://github.com/bcgit/bc-csharp/blob/master/README.md).
