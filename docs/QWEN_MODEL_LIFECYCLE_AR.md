# هوية نموذج Qwen ودورة حياته — Phase C

## النطاق المثبّت

- الاسم الذي يطلبه التطبيق: `qwen3.5:2b`.
- Registry: `https://registry.ollama.ai`.
- مسار manifest: `/v2/library/qwen3.5/manifests/2b`.
- لا توجد في هذا الدليل بصمة SHA-256 ثابتة للـ tag؛ فالـ tag قابل للتغيير، والبصمة تُستخرج وقت التحقق وتُحفظ في pin محلي بعد مطابقة النموذج المثبّت بها.

## آلية التحقق المنفذة على فرع Phase C

1. يُطلب manifest من HTTPS على المضيف الثابت أعلاه، مع تعطيل اتباع التحويلات، وتحديد حد أقصى لحجم الرد.
2. إذا طلب Registry مصادقة Bearer، يقرأ العميل challenge. يجب أن يكون `realm` عبر HTTPS وعلى نفس سلطة Registry تمامًا. يُوقّع طلب token بمفتاح Ed25519 المحلي الذي أنشأه Ollama (`%USERPROFILE%\\.ollama\\id_ed25519`). لا تُرسل بايتات المفتاح الخاص. يتكوّن Authorization من Base64 لمفتاح SSH العام ثم Base64 لـ `ssh.Signature.Blob`؛ أي SSH string لاسم الخوارزمية `ssh-ed25519` يتبعه SSH string للتوقيع Ed25519 الخام (64 بايت). إرسال التوقيع الخام وحده غير متوافق مع بروتوكول Ollama.
3. يحسب العميل SHA-256 من البايتات الخام للـ manifest. إذا أعاد Registry رأس `Docker-Content-Digest`، يجب أن يطابق القيمة المحسوبة.
4. إذا كان الرد manifest عاديًا، تكون بصمة manifest هي البصمة المقبولة من `/api/tags`. وإذا كان الرد manifest list / OCI index، تُقرأ بصمات manifests الأبناء ذات نوع المحتوى المدعوم. ويُسمح أيضًا ببصمة manifest الأم لتوافق إصدارات Ollama التي قد تعرضها في `/api/tags`. كل قيمة مقبولة يجب أن تكون SHA-256 كاملة وموجودة حرفيًا داخل الرد الذي تم التحقق من بصمته.
5. بعد التنزيل أو عند ترقية pin قديم، يجب أن تطابق البصمة التي يعرضها Ollama محليًا إحدى القيم المقبولة من Registry. إذا لم تطابق، يفشل التحقق ولا يُنشأ pin جديد.
6. لا يصبح pin موثوقًا في هذا التطبيق إلا بعد نجاح مطابقة Registry واجتياز health check المحلي. يحفظ pin: اسم النموذج، بصمة النموذج المحلية، مضيف ومسار Registry، نوع manifest، حجم رد manifest، وقت التحقق، بصمة manifest الأم، والبصمات المقبولة من داخله.
7. عند التشغيل اللاحق، يمكن التحقق دون اتصال بمقارنة digest المحلي بالـ pin المحفوظ. لا يُتبع tag متغير تلقائيًا في كل تشغيل ولا يُحدّث pin بصمت.

## المصادقة وحدود الثقة

- خدمة Ollama المحلية مسؤولة عن إنشاء وإدارة مفتاح Ed25519 في مجلد المستخدم. عند صدور تحدي Bearer، إذا كان المفتاح غير موجود أو غير صالح يفشل طلب Registry المغلق افتراضيًا؛ لا ينشئ Layal مفتاحًا بديلًا ولا يرسل المفتاح الخاص. الوصول العام الذي لا يطلب challenge لا يعتمد على المفتاح.
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
| manifest عادي: حساب SHA-256 من bytes ومطابقة `Docker-Content-Digest` عند وجوده | PASS ضمن Windows Build run [44473228618](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/44473228618) |
| manifest list: استخراج بصمات الأبناء وبصمة الأم | PASS ضمن Windows Build run 44473228618؛ وتمت مطابقة digest النموذج المثبت مع إحدى بصمات manifest الفرعية في الاختبار الحي |
| جلب manifest حي من Registry | PASS: HTTP 200 عام دون Bearer challenge؛ جسم JSON وبصمة SHA-256 وحجم manifest (470 بايت) تحققت في Qwen lifecycle run [44473228601](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/44473228601) |
| اختبار صيغة مفتاح Ollama الفعلية | PASS: فتح مفتاح OpenSSH الذي أنشأه Ollama على Windows، إنشاء توقيع SSH محلي، وفك `Signature.Blob` والتحقق من التوقيع بالمفتاح العام؛ لا يُرسل المفتاح الخاص أو probe token إلى الشبكة |
| رفض اختلاف رأس `Docker-Content-Digest` عن البايتات | PASS ضمن Windows Build run 44473228618 |
| رفض التحويلات والأنواع غير المدعومة والردود الكبيرة | PASS ضمن Windows Build run 44473228618 |
| انتهاء مهلة Registry أثناء قراءة body بطيئة/معلّقة | PASS باختبارات regression ضمن Windows Build run 44473228618 |
| Windows build واختبارات C# وفحوص الواجهة وحزمة التوزيع واختبار دورة حياة المثبّت | PASS: run [44473228618](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/44473228618) |
| Full Windows PowerShell acceptance ورفع الأدلة | PASS: run [44473228725](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/44473228725) |
| NuGet advisory/license audit وfull-history Gitleaks | PASS: run [44473228604](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/44473228604) |
| قبول Bearer token حي من Registry عند صدور 401 | لم يُصدر Registry تحديًا في الاختبار العام (أعاد 200)، لذلك لا ندّعي قبول Bearer حيًا. مسار الإنتاج واختبار 401 الاصطناعي يغطيان التوقيع وإعادة الطلب؛ مفتاح Ollama الحقيقي اجتاز local signing probe |
| تنزيل حقيقي على Windows، الإلغاء والاستئناف، digest محلي، وhealth check | PASS: أُلغي التنزيل عند 14,583,224 بايت ثم اكتمل الاستئناف؛ digest المثبت `0689d44085e06d165161a8a9a1731344278cfb5aade63a3c3dbdb48ab54b130a` طابق Registry؛ نجح اختبار المحادثة |
| update/rollback على نسخة Models محلية تجريبية معزولة | PASS: `ollama cp/rm` استعاد النسخة السابقة ببصمتها نفسها ثم اجتاز اختبار الصحة مرة أخرى |
| قياس الحجم الفعلي المنزّل ومواءمته مع المساحة المحجوزة | PASS: مجموع blobs الفعلي `2,683,398,180` بايت (أقل من حد 3.1 GB)؛ manifest الأب `0b28bbb3449dcb370c881ce3afacc557047fc7ecbcacaeb280c92c2bf9f11beb` وحجمه 470 بايت |
| قياس RAM/VRAM/سرعة inference ومقارنة variants | مؤجل؛ لا يوجد benchmark قابل للتكرار على جهاز مستهدف بعد |

## الحالة الحالية

**Phase C merged and CI-accepted.** PR [#205](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/pull/205) دُمج في `main` على merge commit `d85a06b9a64f9fa931779e00957b62abee6b7aab` بتاريخ 19 نوفمبر 2026. نجحت Windows Build وحزمة التوزيع واختبارات الوحدة والواجهة ودورة حياة المثبّت، ونجح Full Windows acceptance وSecurity/Supply-chain وQwen live lifecycle على رأس PR قبل الدمج. إعادة فحص Security بعد الدمج نجحت؛ إعادة Windows Build على merge commit كانت قيد التنفيذ وقت تحديث الوثيقة. 

أدلة Qwen الحية تخص خادم Ollama على Windows ومجلد Models مؤقتًا معزولًا، ولا تمثل قياس أداء على جهاز المستخدم. الـRegistry العام أعاد HTTP 200 دون challenge؛ لذلك لا يُدّعى نجاح تبادل Bearer موقّع على Registry. الـlocal-key probe يثبت قدرة مفتاح Ollama الحقيقي على التوقيع محليًا فقط. لا تمثل آلية Registry توقيعًا مستقلًا من ناشر Qwen. قبول الجهاز المستهدف، وتوقيع الكود، ومراجعة حقوق الأصول/التراخيص، ومقارنة RAM/VRAM تظل بوابات منفصلة.

## مراجع التنفيذ الرسمية

- Ollama Registry challenge, token request, and signed authorization: [`server/auth.go`](https://github.com/ollama/ollama/blob/main/server/auth.go) and [`auth/auth.go`](https://github.com/ollama/ollama/blob/main/auth/auth.go).
- Ollama manifest media types and child-manifest digest calculation: [`manifest/manifest.go`](https://github.com/ollama/ollama/blob/main/manifest/manifest.go).
- Ollama `/api/tags` selected-child behavior for manifest lists: [`server/model_list.go`](https://github.com/ollama/ollama/blob/main/server/model_list.go).
- SSH.NET package version and upstream license: [NuGet SSH.NET 2026.0.1](https://www.nuget.org/packages/SSH.NET/2026.0.1) and [upstream LICENSE](https://github.com/sshnet/SSH.NET/blob/develop/LICENSE).
- SSH.NET's transitive BouncyCastle package and its MIT / modified-Bzip2 Apache 2.0 note: [NuGet BouncyCastle.Cryptography 2.7.0](https://www.nuget.org/packages/BouncyCastle.Cryptography/2.7.0) and [upstream README](https://github.com/bcgit/bc-csharp/blob/master/README.md).

## اختيار Qwen variants حسب موارد الجهاز — PR #207

دُمج PR #207 في `main` على merge commit `ae7b94e513fd355afd4d8430b4ca1c903754e665`. يحافظ هذا التحديث على دليل دورة الحياة الحية الأصلي من PR #205 كما هو: الاختبار الفعلي على Windows استخدم `qwen3.5:2b` في مجلد Ollama مؤقت معزول. دعم variants الأخرى أصبح ضمن الكود المدمج، لكن لا نعيد كتابة الدليل التاريخي أو ندّعي أن كل tag تم تنزيله واختباره حيًا:

- قائمة tags المسموح بها: `qwen3.5:0.8b` و`qwen3.5:2b` و`qwen3.5:4b` و`qwen3.5:9b`. مسارات Registry تُبنى من tag بعد التحقق من القائمة البيضاء؛ لا يُقبل أي tag عشوائي.
- لكل tag ملف pin منفصل: `qwen3.5-0.8b.json` أو `qwen3.5-2b.json` أو `qwen3.5-4b.json` أو `qwen3.5-9b.json`. يحوي كل ملف هوية النموذج وبيانات manifest التي اجتازت تحقق TLS/Registry وdigest النموذج المحلي. تغيير الاختيار لا يعيد استخدام pin الخاص بـ tag آخر.
- يقيس Windows RAM المتاحة من `GlobalMemoryStatusEx`. إذا توفر `nvidia-smi`، يقرأ VRAM الحرة لكل محول NVIDIA ويختار أكبر سعة حرة على محول منفرد. لا تُجمع ذاكرة محولين منفصلين ولا تُستخدم قيمة AdapterRAM من WMI كأنها ذاكرة حرة.
- ميزانية GPU المحافظة هي 150% من سقف حجم التنزيل المرجعي، مضافًا إليها 1 GiB لتشغيل النموذج، مع 4 GiB على الأقل من RAM النظام المتاحة. ميزانية CPU هي ضعف سقف التنزيل مضافًا إليه 2 GiB تشغيل. عند غياب VRAM موثوقة، يعتمد الاختيار على RAM/CPU فقط.
- حتى تتوفر قياسات أداء قابلة للتكرار على CPU، لا يختار مسار CPU-only نموذج 9B. يسمح 9B فقط عند وجود VRAM حرة مقاسة على محول NVIDIA واحد تكفي ميزانيته. عند تعذر قياس RAM الحرة أو عدم كفايتها حتى لأصغر variant، يظل وضع الإعداد الكامل محظورًا ولا يُخمّن التطبيق أن الجهاز قادر.
- يعيد المعالج قياس RAM/VRAM عند ضغط «ابدأ». يتم حفظ tag المختار قبل تنزيل Ollama، ويستخدم الاختيار نفسه في Registry manifest و`ollama pull` وhealth check وملف pin وعمليات update/rollback.
- سجل تعافي واحد باسم `qwen3.5.update.json` يعمل على مستوى عائلة Qwen، ويحتوي `ModelId` للنسخة السابقة. هذا يتيح استرداد tag سابق حتى إذا تغير الاختيار بين تشغيلين. يتعرف أيضًا على اسم سجل 2B القديم `qwen3.5-2b.json.update.json`. إذا وُجد السجل الحديث والقديم معًا يتوقف التطبيق بدل التخمين بين حالتي استرداد.
- اختبارات السياسة تغطي حدود الذاكرة ورفض tag غير مسموح به، لكنها اختبارات أجهزة اصطناعية. لا تُعد قياسًا لسرعة التوليد أو زمن الاستجابة على جهاز المستخدم.
- بوابات CI على رأس PR #207 نجحت: [Windows Build/installer — 44504262880](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/44504262880)، [PowerShell acceptance — 44504262948](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/44504262948)، [Security/Supply-chain — 44504263055](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/44504263055)، [Qwen live lifecycle — 44504263151](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/44504263151). نجح فحص Security بعد الدمج في [44505850946](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/44505850946). إعادة Windows Build على merge commit [44505851078](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/actions/runs/44505851078) كانت لا تزال في خطوة التغليف ورفع الأدلة أثناء تحديث الوثيقة.

**مراجع tags وأحجام التنزيل المرجعية:** https://ollama.com/library/qwen3.5/tags. هذه أحجام تنزيل للأوزان وليست حدًا مباشرًا لذاكرة التشغيل، ولذلك تضيف سياسة Layal هامش ذاكرة مستقلًا. الأداء الفعلي ما زال يحتاج benchmark على أجهزة تمثيلية قبل إعلان أن variant بعينه هو الأسرع أو الأنسب.
