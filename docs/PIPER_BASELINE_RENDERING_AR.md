# توليد خط الأساس الصوتي Piper على Windows

**الحالة:** أداة محلية جاهزة للتجربة بعد سحب فرع التنفيذ. لا تغيّر التطبيق أو إعدادات الإنتاج، ولا تنزّل نماذج أو حزم، ولا تستخدم الميكروفون.

## ما الذي تفعله الأداة؟

يشغّل الملف tools/evaluation/render_piper_baseline.py ملفات Piper المثبتة بالفعل بواسطة Layal لإنتاج مجموعة المقارنة المرجعية:

- 18 عبارة ثابتة من tests/TestAssets/Speech/voice-persona-evaluation.json.
- 8 أنماط الإلقاء الموجودة في VoiceDeliveryStylePolicy.
- الناتج الكامل: **144 ملف WAV** (18 × 8).
- ملف run.json متوافق مع tools/evaluation/score_tts_candidates.py.

تُحفظ كل النتائج تحت مجلد التجربة داخل مجلد العمل الذي حدده المستخدم؛ ولا تُكتب أي عينة في مجلد موارد التطبيق.

## تنبيه بشأن معنى خط الأساس

هذا القياس يسجل سلوك Piper الحالي، ولا يعني أنه يحقق شخصية ليال المستهدفة:

- النص العربي يمر إلى ar_JO-kareem-medium، وهو صوت عربي أردني، وليس نموذجًا مصممًا خصيصًا للهجة المصرية القاهرية.
- النص الإنجليزي يمر إلى en_US-lessac-medium.
- الكلام المختلط يُقسّم إلى مقاطع عربية/إنجليزية ويستخدم النموذج المقابل لكل مقطع. النموذجان صوتان مختلفان، لذلك لا يوجد ضمان لهوية متحدثة واحدة عبر اللغات.
- الأنماط الثمانية تغيّر length_scale وsentence_silence طبقًا لسياسة التطبيق. هذا تغيير في السرعة والوقفات، وليس تحكمًا عاطفيًا أو تغييرًا في خامة الصوت.
- تُترك مراجعة الترخيص وحقوق الصوت والقبول البشري بحالة pending. لذلك لا يمكن لهذا التقرير وحده اعتماد Piper كصوت إنتاج نهائي.

## تجهيز نسخة المستودع محليًا داخل AI

لو نسخة المستودع غير موجودة على جهازك، يمكن وضعها بجانب ملفاتك الحالية داخل مجلد AI دون المساس بملفات Word الموجودة:

~~~powershell
$ai = Join-Path $env:USERPROFILE 'Desktop\JobApplicationSender\AI'
$repo = Join-Path $ai 'Layal-Windows-Agent'

git clone --branch feat/piper-baseline-renderer https://github.com/MohamedElsayedFathy/Layal-Windows-Agent.git $repo
Set-Location $repo
~~~

لو عندك clone سابق للمستودع، لا تنشئ نسخة ثانية؛ افتح جذر النسخة الموجودة واسحب فرع feat/piper-baseline-renderer إليها بعد التأكد من حفظ أي تغييرات محلية.

## شروط ما قبل التشغيل

1. اسحب فرع feat/piper-baseline-renderer أو استخدم نسخة المستودع بعد دمجه.
2. افتح PowerShell من جذر مستودع Layal-Windows-Agent (المجلد الذي يحتوي tools وtests).
3. تأكد من تجهيز موارد الصوت المحلي في Layal مسبقًا. هذه الأداة لا تصلح تثبيتًا ناقصًا ولا تعيد تنزيله.
4. Python 3.10+ مطلوب. الأداة تستخدم المكتبات القياسية فقط ولا تحتاج pip packages إضافية.

## مسارات موارد Piper المثبتة

في التشغيل العادي، يخزن Layal الموارد هنا:

~~~powershell
$localAi = Join-Path $env:LOCALAPPDATA 'Layal\LocalAi'
$piperRoot = Join-Path $localAi 'tools\piper'
$modelsDir = Join-Path $localAi 'speech-models'
~~~

عند تشغيل التطبيق داخل نطاق قبول معزول، يحترم التطبيق متغير البيئة LAYAL_ACCEPTANCE_ROOT. في هذه الحالة استخدم:

~~~powershell
$localAi = Join-Path $env:LAYAL_ACCEPTANCE_ROOT 'LocalAppData\Layal\LocalAi'
$piperRoot = Join-Path $localAi 'tools\piper'
$modelsDir = Join-Path $localAi 'speech-models'
~~~

يجب أن يحتوي piperRoot على piper\piper.exe وملفات DLL/ORT المرافقة، وأن يحتوي modelsDir على ملفات النموذجين وملفي JSON.

## تجربة قصيرة أولًا (8 عينات)

الأفضل تشغيل عبارة واحدة عبر الأنماط الثمانية كاختبار أولي، ثم تنفيذ المجموعة كاملة في مجلد جديد:

~~~powershell
$ai = Join-Path $env:USERPROFILE 'Desktop\JobApplicationSender\AI'
$runId = 'piper-smoke-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
$runDir = Join-Path $ai "artifacts\tts-evaluation\$runId"

py -3 tools\evaluation\render_piper_baseline.py --piper-root $piperRoot --models-dir $modelsDir --output-dir $runDir --limit-cases 1
if ($LASTEXITCODE -ne 0) { throw 'فشل اختبار Piper القصير؛ راجع رسالة الخطأ.' }

py -3 tools\evaluation\score_tts_candidates.py (Join-Path $runDir 'run.json') --report-json (Join-Path $runDir 'report.json')
~~~

سيظهر أن التغطية جزئية (8 من 144)، وهذا متوقع لأننا طلبنا عبارة واحدة فقط.

## التشغيل الكامل (144 عينة)

بعد نجاح التجربة القصيرة، أنشئ مجلدًا جديدًا وشغّل دون --limit-cases:

~~~powershell
$ai = Join-Path $env:USERPROFILE 'Desktop\JobApplicationSender\AI'
$runId = 'piper-baseline-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
$runDir = Join-Path $ai "artifacts\tts-evaluation\$runId"

py -3 tools\evaluation\render_piper_baseline.py --piper-root $piperRoot --models-dir $modelsDir --output-dir $runDir
if ($LASTEXITCODE -ne 0) { throw 'فشل توليد عينات Piper؛ راجع generationStatus وfailure داخل run.json.' }

py -3 tools\evaluation\score_tts_candidates.py (Join-Path $runDir 'run.json') --report-json (Join-Path $runDir 'report.json')
~~~

المخرجات:

~~~text
AI/
  artifacts/
    tts-evaluation/
      piper-baseline-<timestamp>/
        run.json
        report.json
        samples/
          piper-baseline__egy-01__natural.wav
          ...
~~~

## فحوص النزاهة والسلوك

قبل تشغيل Piper تتحقق الأداة من:
- ملف piper.exe وكل ملفات DLL/ORT الستة التي يثبتها التطبيق.
- ملفي ONNX العربي والإنجليزي وملفي الإعداد JSON.
- أحجام الملفات وبصمات SHA-256 المطابقة حرفيًا لقيم LocalVoiceService.cs.
- أن معدل العينة في ملفي الإعداد 22050 Hz.
- عدم المرور عبر shell، ووجود مهلة لكل استدعاء، وتنظيف ملفات المقاطع المؤقتة.

عند نقص ملف أو اختلاف بصمته، تتوقف الأداة قبل تشغيل النموذج. لا تحاول تنزيل أو استبدال الملف المفقود.

## قراءة القياسات

- renderTimeMs: الزمن الكلي لتوليد مقاطع العبارة ودمجها وحساب بصمة WAV.
- timeToFirstAudioMs: الزمن من بداية العينة حتى يصبح أول مقطع لغوي جاهزًا؛ هذا يقارب زمن جاهزية أول جزء في مسار التطبيق، وليس مقياس streaming token latency.
- realTimeFactor: يحسبه scorer من زمن التوليد ومدة WAV؛ الأقل أسرع.
- تتضمن العينات voiceRoutes لتوضيح استخدام الصوت العربي أو الإنجليزي في كل مقطع.

أداة التقييم تحسب مؤشرات WAV وتتحقق من سلامة الملفات. لكنها لا تحكم على جمال الصوت أو صحة اللهجة أو ثبات الهوية. يجب إضافة تقييم بشري مستقل لاحقًا، ولا تملأ الدرجات أو حالات الحقوق تلقائيًا.

## ماذا لو فشل التشغيل؟

- **ملف غير موجود أو SHA غير مطابق:** أعد تجهيز موارد الصوت المحلي من داخل التطبيق بعد مراجعة السبب، ثم أعد تشغيل الأداة. لا تتجاوز فحص البصمة.
- **Piper انتهى برمز غير صفر:** احتفظ برسالة الخطأ وافحص التثبيت المحلي؛ لا تحاول تنزيل محرك بديل من مصدر غير مثبت.
- **اختلاف تنسيق WAV بين المقاطع:** ترفض الأداة الدمج بدل إعادة أخذ العينات أو تغيير خصائص الصوت بصمت.
- **نفاد مساحة القرص:** حرّر مساحة قبل إعادة التشغيل؛ مجلد الإخراج الموجود لا يُستبدل أو يُحذف تلقائيًا.
- **توقف في منتصف المجموعة:** يظل run.json موجودًا بحالة generationStatus: failed وعدد العينات المكتملة. لا تتعامل مع مجموعة غير مكتملة على أنها اجتازت الاختبار.

## ما لم تفعله الأداة

- لا تنزّل أوزان أو حزم ولا تتصل بالإنترنت.
- لا تسجل صوت المستخدم ولا تقرأ ملفات شخصية.
- لا تغيّر Piper أو إعدادات Layal أو ملفات الإنتاج.
- لا تدّعي نجاح جودة الصوت قبل الاستماع والمراجعة البشرية.
