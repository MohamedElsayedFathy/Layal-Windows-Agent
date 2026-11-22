# تشغيل QwenCleo-ASR كمرشح محلي — Phase D

**الحالة:** أداة المقارنة الاختيارية موجودة في `tools/evaluation/run_qwencleo_benchmark.py`. لا توجد حتى الآن نتائج QwenCleo على تسجيلات Layal المصرح بها؛ لا تُعد أرقام بطاقة النموذج نتائج للمشروع، ولا يتغير محرك الإنتاج بسبب هذا المرشح.

## حدود الاستخدام والقرار الحالي

- QwenCleo-ASR مرشح ASR اختياري مبني على Qwen3-ASR-1.7B، وموجّه أساسًا للمصرية والخلط العربي/الإنجليزي.
- بطاقة المصدر تعلن Apache-2.0، وتذكر أن الضبط الدقيق استخدم مئات الساعات من البودكاست المصري، لكنها لا تقدم في البطاقة التي تمت مراجعتها جردًا تفصيليًا لمصادر بيانات التدريب أو إثباتات موافقاتها وحقوقها. لذلك حالة مراجعة الحقوق ومصدر بيانات التدريب في تقارير Layal تبقى `pending`.
- البطاقة تصرح بأن النموذج غير محسّن للصوت الإنجليزي الخالص. يمرر المشغّل فئة `en` إلى الاكتشاف التلقائي، ويعرضها كفئة تحتاج تفسيرًا منفصلًا.
- لم يُعتمد النموذج إنتاجيًا، ولم يُربط بـ Layal أو Complete Setup، ولا يستبدل Whisper. لا تنزيل تلقائي، ولا server/vLLM، ولا اتصال ASR سحابي.
- يلزم تشغيله وWhisper على **نفس ملفات WAV** وبنفس المراجع البشرية المعتمدة قبل المقارنة. لا تستخدم مجموعة النصوص ذات الثلاثين حالة كأنها مجموعة صوتية.

المراجع: [بطاقة QwenCleo-ASR](https://huggingface.co/mohammedaly22/QwenCleo-ASR) و[المستودع البرمجي](https://github.com/MohammedAly22/QwenCleo-ASR).

## متطلبات ما قبل التشغيل

1. أكمل بروتوكول [تجهيز وجمع مجموعة الكلام](SPEECH_DATA_COLLECTION_AR.md): موافقة فعلية بنطاق `research-evaluation`، تسجيل WAV، ومراجعة بشرية واعتماد transcript لكل عينة.
2. احتفظ بالملفات والصوت ونموذج الموافقة محليًا خارج GitHub. لا تستخدم أي بيانات تعريفية في manifest.
3. جهّز نسخة محلية مستقلة من نموذج QwenCleo من مصدره الرسمي، وحدد commit SHA مكوّنًا من 40 خانة للإصدار الذي جرى تنزيله. يطلب المشغّل مجلدًا محليًا موجودًا ولا ينزّل نموذجًا أو حزمًا.
4. يجب أن يحتوي المجلد على `config.json` وملفات الأوزان. يفحص المشغّل أن البنية Qwen3-ASR، ويرفض الروابط الرمزية وWindows reparse points، ويحسب SHA-256 لكل ملف. إذا كان اسم مجلد snapshot نفسه هو commit SHA، يتحقق من مطابقته للقيمة المعطاة. هذه الفحوص تسجل هوية الملفات وتطابق اسم snapshot فقط؛ لا تصادق تشفيريًا على أن المجلد جاء من مستودع QwenCleo الرسمي أو أن بصماته تطابق خادم Hugging Face. تحقق من مصدر التنزيل بنفسك، واعتبر revision الممرر تصريحًا من المشغّل، لا إثبات منشأ مستقلًا.
5. جهّز بيئة Python تقييمية متوافقة مع الجهاز تحتوي على `torch` و`qwen-asr` واعتمادياتهما. المشغّل لا ينفذ `pip install`، ولا يغير بيئة Python الخاصة بالإنتاج. استخدم إصدار PyTorch الملائم لتعريف GPU إن وجد؛ لا تثبت vLLM أو server extras لهذا المسار.

## تشغيل المقارنة

مثال PowerShell من جذر المستودع بعد تشغيل Whisper على نفس المجموعة:

~~~powershell
$ModelDir = "D:\LayalSpeechEvaluation\Models\QwenCleo"
$ModelRevision = "ضع-هنا-40-خانة-لـ-HuggingFace-commit"
$WhisperScoringManifest = "D:\LayalSpeechEvaluation\Reports\whisper-benchmark-...scoring-manifest.json"
$OutputDir = "D:\LayalSpeechEvaluation\Reports\qwencleo"
python tools/evaluation/run_qwencleo_benchmark.py --model-dir $ModelDir --model-revision $ModelRevision --manifest $WhisperScoringManifest --output-dir $OutputDir --device auto
~~~

خيارات الجهاز: `auto` يختار CUDA إن كان متاحًا وإلا CPU؛ `cuda:0` يفرض GPU؛ `cpu` يفرض المعالج. لا يعني اختيار `auto` أن الذاكرة كافية؛ إذا فشل تحميل النموذج فراجع RAM/VRAM وسجل الخطأ ولا تغيّر إعدادات الإنتاج لتجاوزه.

يمرر المثال ملف scoring الناتج من Whisper كمدخل، حتى يحتفظ الناتج بفرضية `whisper_cpp_base` ويضيف `qwencleo_asr_17b`. بذلك يمكن قياس المحركين على نفس الحالات:

~~~powershell
python tools/evaluation/score_speech_transcripts.py --manifest "D:\LayalSpeechEvaluation\Reports\qwencleo\qwencleo-benchmark-...scoring-manifest.json" --engine whisper_cpp_base
python tools/evaluation/score_speech_transcripts.py --manifest "D:\LayalSpeechEvaluation\Reports\qwencleo\qwencleo-benchmark-...scoring-manifest.json" --engine qwencleo_asr_17b
~~~

يعرض التقرير WER وCER وCritical-token error rate لكل تنوع على حدة. راجع خصوصًا النفي والأرقام وأسماء الملفات والكلمات الإنجليزية داخل الكلام المصري. لا تقارن نتائج المحركين إذا لم تتطابق معرفات العينات وبصمات WAV والمراجع المعتمدة.

## ما يسجله المشغّل

- commit النموذج الذي أدخله المشغّل، وSHA-256 والحجم النسبي لكل ملف محلي في snapshot.
- إصدار `torch` و`qwen-asr` والجهاز وdtype، وزمن تحميل النموذج وزمن كل عينة وReal-Time Factor، وذروة CUDA allocated memory عندما تتاح.
- ملف تقرير JSON وملف scoring manifest مستقلان؛ لا يعدّل manifest المصدر.
- يزيل أي فرضية قديمة لـ `qwencleo_asr_17b` عند فشل عينة في التشغيل الحالي، ولا يحتفظ بمسارات الملفات المحلية في رسائل الفشل.
- يبقي أهلية الإنتاج `false`، ومراجعة الترخيص/مصدر بيانات التدريب `pending`.

تُكتب التقارير في مجلد الإخراج المحلي؛ فهي تحتوي تفريغات مشتقة من صوت المتحدث، لذا لا ترفعها إلى GitHub أو PR أو artifacts عامة.

## اختبارات وحدود القبول

اختبارات `tests/python/test_qwencleo_benchmark.py` تستخدم أوزانًا وبيانات WAV مصطنعة، وحقنًا لمحرك وهمي. تثبت التحقق من بصمات snapshot وسياسة الملفات المحلية وحماية الموافقة والمراجعة وتنقية scoring manifest؛ **لا** تثبت جودة QwenCleo، أو توافق CUDA الفعلي، أو WER/CER، أو الحقوق القانونية للبيانات التي دُرّب عليها النموذج.

بوابات القبول التي تظل مفتوحة:
- [ ] مراجعة مستقلة لترخيص ملفات النموذج وحقوق/مصادر بيانات التدريب.
- [ ] تشغيل Whisper ثم QwenCleo على التسجيلات نفسها بعد الموافقة واعتماد النصوص البشرية.
- [ ] نتائج WER/CER وCritical-token لكل من `ar-EG` و`ar` و`en` و`mixed`، مع إظهار حجم العينة والفشل.
- [ ] قياس الجهاز الأساسي والمتقدم، بما يشمل RAM/VRAM وزمن تحميل النموذج وRTF.
- [ ] قرار موثق بشأن استمرار QwenCleo كمرشح؛ لا تغيير في محرك Layal الإنتاجي من هذه الأداة وحدها.
