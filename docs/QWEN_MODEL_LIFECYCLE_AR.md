# هوية نموذج Qwen ودورة حياته — Phase C

## النطاق المثبّت

- الاسم الذي يطلبه التطبيق: \`qwen3.5:2b\`.
- Registry: \`https://registry.ollama.ai\`.
- مسار manifest: \`/v2/library/qwen3.5/manifests/2b\`.
- لا توجد في هذا الدليل بصمة SHA-256 ثابتة للـ tag؛ فالـ tag قابل للتغيير، والبصمة تُستخرج وقت التحقق وتُحفظ في pin محلي بعد مطابقة النموذج المثبّت بها.

## آلية التحقق المنفذة على فرع Phase C

1. يُطلب manifest من HTTPS على المضيف الثابت أعلاه، مع تعطيل اتباع التحويلات، وتحديد حد أقصى لحجم الرد.
2. إذا طلب Registry مصادقة Bearer، يقرأ العميل challenge. يجب أن يكون \`realm\` عبر HTTPS وعلى نفس سلطة Registry تمامًا. يُوقّع طلب token بمفتاح Ed25519 المحلي الذي أنشأه Ollama (\`%USERPROFILE%\\.ollama\\id_ed25519\`). لا تُرسل بايتات المفتاح الخاص؛ يُرسل فقط رأس المصادقة الذي يحتوي على المفتاح العام المرمّز والتوقيع.
3. يحسب العميل SHA-256 من البايتات الخام للـ manifest. إذا أعاد Registry رأس \`Docker-Content-Digest\`، يجب أن يطابق القيمة المحسوبة.
4. إذا كان الرد manifest عاديًا، تكون بصمة manifest هي البصمة المقبولة من \`/api/tags\`. وإذا كان الرد manifest list / OCI index، تُقرأ بصمات manifests الأبناء ذات نوع المحتوى المدعوم. ويُسمح أيضًا ببصمة manifest الأم لتوافق إصدارات Ollama التي قد تعرضها في \`/api/tags\`. كل قيمة مقبولة يجب أن تكون SHA-256 كاملة وموجودة حرفيًا داخل الرد الذي تم التحقق من بصمته.
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
- وجود pin سابق مع غياب النموذج المحلي: يتوقف الإعداد بدل تنزيل أحدث محتوى للـ tag المتغير تلقائيًا. مسار reset/update صريح وآمن لم يُنفّذ بعد.
- pin موجود لكن digest المحلي تغير: يُرفض النموذج ولا يُستبدل pin تلقائيًا.
- لم تُضف بعد اختيارات variants أو تحديد GPU/VRAM آليًا؛ ذلك مؤجل لحين جمع benchmarks قابلة للتكرار على أجهزة مستهدفة.
- لا يمنح هذا المسار Qwen صلاحية تنفيذ المهام. يبقى تخطيط/تنفيذ الأوامر عبر \`NativeLanguageParser\` و\`NativeAgentOrchestrator\` والموافقات والأدوات المسموح بها.

## مصفوفة التحقق

| الاختبار | الحالة |
|---|---|
| manifest عادي: حساب SHA-256 من bytes ومطابقة `Docker-Content-Digest` عند وجوده | نجح ضمن Windows CI على الرأس السابق؛ إعادة تشغيل CI مطلوبة للرأس الذي يتضمن إصلاح timeout |
| manifest list: استخراج بصمات الأبناء وبصمة الأم | نجح ضمن Windows CI على الرأس السابق؛ إعادة تشغيل CI مطلوبة للرأس الحالي |
| challenge Bearer ثم إعادة طلب manifest بالتوكن | نجح كاختبار retry بمزوّد token اصطناعي؛ لا يختبر توقيع Ed25519 الحقيقي |
| رفض اختلاف رأس `Docker-Content-Digest` عن البايتات | نجح ضمن Windows CI على الرأس السابق؛ إعادة تشغيل CI مطلوبة للرأس الحالي |
| رفض التحويلات والأنواع غير المدعومة والردود الكبيرة | نجح ضمن Windows CI على الرأس السابق؛ إعادة تشغيل CI مطلوبة للرأس الحالي |
| انتهاء مهلة Registry أثناء قراءة body بطيئة/معلّقة | أُضيف اختبار regression بإعداد مهلة قصيرة؛ ينتظر نتيجة CI على الرأس المحدّث |
| اختبار توقيع Ed25519 بمفتاح Ollama فعلي والتحقق من token على Registry الحي | لم يُنفّذ؛ يحتاج Windows profile مُهيأ بواسطة Ollama |
| تنزيل حقيقي على Windows، الإلغاء والاستئناف، إثبات رفض digest مختلف، health check فعلي | لم يُنفّذ |
| update/rollback على نسخة Models محلية تجريبية معزولة | لم يُنفّذ |
| قياس الحجم الفعلي المنزّل ومواءمته مع المساحة المحجوزة | لم يُنفّذ؛ الحجم الدقيق يظل غير مثبت حتى القياس |
| قياس RAM/VRAM/سرعة inference ومقارنة variants | مؤجل؛ لا يوجد benchmark قابل للتكرار بعد |

## الحالة الحالية

الكود والاختبارات موجودة على الفرع `feature/phase-c-qwen-registry-digest`، ولم تُدمج في `main` بعد. نجحت الاختبارات السابقة وWindows/security CI على الرأس `601f60e044067056c83bd4f17c6353d94af2cf2a` (runs 44301802442 و44301802443 و44301803124). أُصلحت مهلة قراءة manifest وأُضيف اختبار regression، وتنتظر هذه التغييرات إعادة CI. لا يوجد في هذا السجل digest حي أو حجم تنزيل فعلي أو health-check ناجح على جهاز المستخدم. لا تُعلن Phase C مكتملة قبل اجتياز CI المحدّث والاختبارات الحية المنعزلة وتوثيق نتائجها.
## مراجع التنفيذ الرسمية

- Ollama Registry challenge, token request, and signed authorization: [`server/auth.go`](https://github.com/ollama/ollama/blob/main/server/auth.go) and [`auth/auth.go`](https://github.com/ollama/ollama/blob/main/auth/auth.go).
- Ollama manifest media types and child-manifest digest calculation: [`manifest/manifest.go`](https://github.com/ollama/ollama/blob/main/manifest/manifest.go).
- Ollama `/api/tags` selected-child behavior for manifest lists: [`server/model_list.go`](https://github.com/ollama/ollama/blob/main/server/model_list.go).
- SSH.NET package version and upstream license: [NuGet SSH.NET 2026.0.1](https://www.nuget.org/packages/SSH.NET/2026.0.1) and [upstream LICENSE](https://github.com/sshnet/SSH.NET/blob/develop/LICENSE).
- SSH.NET's transitive BouncyCastle package and its MIT / modified-Bzip2 Apache 2.0 note: [NuGet BouncyCastle.Cryptography 2.7.0](https://www.nuget.org/packages/BouncyCastle.Cryptography/2.7.0) and [upstream README](https://github.com/bcgit/bc-csharp/blob/master/README.md).
