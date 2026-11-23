# اختبار Whisper على صوت مصري اصطناعي — Phase D

**الحالة:** أدوات تجهيز وتشغيل smoke subset مضافة على فرع تطوير مستقل. لم تُشغّل بعد على موارد Whisper الفعلية في جهاز المستخدم.

## الغرض والحدود

المصدر التجريبي هو [Synthetic Egyptian Speech Dataset](https://huggingface.co/datasets/MohamedGomaa30/Synthetic-Egy-Speech-Dataset)، ويعرض 1,000 عينة صوت مصرية اصطناعية من أربعة مولدات TTS. بطاقة المصدر تعلن ترخيص MIT.

هذا المصدر مناسب لاختبار:
- قراءة ملفات WAV والتحقق من سلامتها.
- تشغيل نسخة Whisper المثبتة محليًا.
- استخراج النصوص وقياس زمن التشغيل وReal-Time Factor.
- التأكد من عمل أدوات التقييم المحلية دون تسجيل صوت المستخدم.

ولا يصلح وحده لقياس:
- دقة Whisper على أصوات بشرية حقيقية.
- جودة فهم المحادثات الطبيعية أو التداخل بين المتحدثين.
- WER/CER ذهبي؛ فالنص المنشور هو prompt التوليد وليس تفريغًا بشريًا مستقلًا للصوت.
- أداء المصرية/الإنجليزية/الفصحى/المختلط كحزمة واحدة؛ المجموعة مصرية اصطناعية فقط.

المجموعة موصوفة بأنها best-selected عبر أربعة مولدات وبمعايير تتضمن WER degradation، لذا فهي ليست عينة غير متحيزة من أداء النطق. الترخيص المعلن في بطاقة البيانات لا يمثل تدقيقًا مستقلًا لحقوق كل مصدر نص أو نموذج TTS upstream؛ لهذا المسار غرض محلي/تجريبي فقط ولا يعيد توزيع التسجيلات.

## مصدر مثبت الإصدار

- Dataset: MohamedGomaa30/Synthetic-Egy-Speech-Dataset
- Revision: bf7ff28bd1737bc78d154ee9f9180a701f88cd1e
- بطاقة المصدر: https://huggingface.co/datasets/MohamedGomaa30/Synthetic-Egy-Speech-Dataset
- الترخيص المعلن: MIT في front matter لملف README.
- الإسناد الوارد في بطاقة المصدر: Mohamed Gomaa Elsafy, Synthetic Egyptian Speech Dataset (2026).

## تنزيل النسخة محليًا

التنزيل اختياري ويحتاج نحو 459 MB وفق بطاقة المصدر. لا تشغّل هذه الخطوة على جهاز محدود المساحة إلا بعد التأكد من المساحة المتاحة.

من PowerShell 7:

~~~powershell
python -m pip install --user huggingface_hub
if ($LASTEXITCODE -ne 0) { throw "huggingface_hub installation failed." }

# افحص عينة واحدة أولًا قبل تنزيل المجموعة الكاملة (459 MB).
@'
from huggingface_hub import hf_hub_download
import wave

sample = hf_hub_download(
    repo_id="MohamedGomaa30/Synthetic-Egy-Speech-Dataset",
    repo_type="dataset",
    filename="audio/egy_0001_lahgtna.wav",
    revision="bf7ff28bd1737bc78d154ee9f9180a701f88cd1e",
)
with wave.open(sample, "rb") as audio:
    details = (audio.getcomptype(), audio.getnchannels(), audio.getframerate(), audio.getsampwidth())
print("Sample WAV:", details)
if details not in {("NONE", 1, 16000, 2), ("NONE", 1, 24000, 2)}:
    raise SystemExit("Sample format must be PCM mono 16-bit at 16 kHz or 24 kHz; stop before downloading the full dataset.")
if details[2] == 24000:
    print("24 kHz source confirmed: preparation converts a working copy to Whisper's 16 kHz input.")
'@ | python -
if ($LASTEXITCODE -ne 0) { throw "Sample WAV format is incompatible; stop here and share the printed details." }

# Conversion from 24 kHz to 16 kHz uses only Python's standard library.

$datasetDir = Join-Path $env:LOCALAPPDATA "Layal\SpeechEvaluation\source\Synthetic-Egy-Speech-Dataset"
New-Item -ItemType Directory -Force -Path $datasetDir | Out-Null
$env:LAYAL_SYNTHETIC_SOURCE = $datasetDir

@'
import os
from huggingface_hub import snapshot_download

snapshot_download(
    repo_id="MohamedGomaa30/Synthetic-Egy-Speech-Dataset",
    repo_type="dataset",
    revision="bf7ff28bd1737bc78d154ee9f9180a701f88cd1e",
    local_dir=os.environ["LAYAL_SYNTHETIC_SOURCE"],
    allow_patterns=["README.md", "metadata.jsonl", "audio/*.wav"],
)
'@ | python -

if ($LASTEXITCODE -ne 0) { throw "Synthetic dataset download failed." }
~~~

لا يحتاج المصدر العام إلى token وفق بطاقة الوصول الظاهرة حاليًا. تنزيله لا يمنح إذنًا بإعادة نشره أو استخدامه في التدريب.

## تجهيز 20 عينة متوازنة

استخدم مساحة الجمع المحلية التي جهزتها سابقًا:

~~~powershell
$collectionRoot = Join-Path $env:LOCALAPPDATA "Layal\SpeechEvaluation\collection-20261123-112104"
$smokeRoot = Join-Path $collectionRoot "synthetic-smoke"
python .\tools\evaluation\prepare_synthetic_speech_smoke.py --source-dir $datasetDir --output-dir $smokeRoot --limit 20 --seed 20261123
if ($LASTEXITCODE -ne 0) { throw "Synthetic smoke subset preparation failed." }
~~~

الأداة:
- تتحقق من هوية بطاقة البيانات وتصريح MIT المعلن ووجود 1,000 صف metadata.
- تختار 20 ملفًا بطريقة متوازنة وقابلة لإعادة الإنتاج عبر أسماء نماذج TTS الأربعة.
- تتحقق من أمان المسارات ووجود الملفات، وتقبل WAV أحادي القناة بصيغة PCM 16-bit أو IEEE-float 32-bit بمعدل 16 أو 24 kHz، مع التحقق من المدة والحجم وصحة بيانات RIFF.
- تحول نسخة العمل من IEEE-float 32-bit إلى PCM 16-bit عند الحاجة، ثم تحول 24 kHz إلى 16 kHz محليًا بمرشح low-pass خماسي النقاط؛ لا تعدّل الملف الأصلي.
- تحفظ بصمة WAV الأصلي والمحوّل ومعدل العينة والتحويل المستخدم في manifest.
- لا تنشئ موافقة بشرية أو speakerId، ولا تضع الحالات في manifest التقييم البشري.

تستخدم عملية التحويل مرشح low-pass خماسي النقاط `[1, 4, 6, 4, 1] / 16` قبل خفض معدل العينة بنسبة 3:2. التنفيذ يعتمد على مكتبة Python القياسية ولا يحتاج تثبيت حزمة إضافية. إذا كان معدل العينة أو ترميز WAV مختلفًا عن الصيغتين المدعومتين، لا تتجاوز الفحص ولا تغيّر الملف يدويًا؛ أرسل رسالة الخطأ.

## تشغيل Whisper smoke test

يشترط وجود موارد Whisper المثبتة من Layal في مجلد LocalAi نفسه:
- أرشيف Whisper المثبت ببصمته.
- الملفات المستخرجة المطابقة للأرشيف.
- نموذج ggml-base.bin ببصمته المثبتة.

التشغيل لا ينزّل نموذجًا أو runtime، ولا يستخدم Cloud ASR:

~~~powershell
$layalDataRoot = Join-Path $env:LOCALAPPDATA "Layal\LocalAi"
python .\tools\evaluation\run_synthetic_speech_smoke.py --data-root $layalDataRoot --manifest (Join-Path $smokeRoot "synthetic-smoke-manifest.json")
if ($LASTEXITCODE -ne 0) { throw "Synthetic Whisper smoke test did not complete successfully." }
~~~

يصدر التقرير في:
<LocalAi>\evaluation\whisper-synthetic-smoke\

يحتوي التقرير على نص prompt والتفريغ الذي أنتجه Whisper، ومحرك TTS المصدر، وبصمات WAV وWhisper وزمن التشغيل وRTF.

**لا يصدر runner scoring manifest ولا يحسب WER/CER.** كما يرفض أي manifest لا يحمل evaluationMode=synthetic-speech-smoke-only وscoreEligibility=not-eligible-for-WER-CER.

## الفصل عن مجموعة البيانات البشرية

لا تعدّل tests/TestAssets/Speech/speech-reference-set.json ولا مساحة التسجيل الأصلية. لا تمرّر synthetic-smoke-manifest.json إلى score_speech_transcripts.py أو run_whisper_benchmark.py.

نتائج هذه الخطوة تُسمّى Synthetic Smoke Test فقط. قياس WER/CER النهائي يحتاج WAV بشريًا مرخصًا وموافقًا على استخدامه، مع transcript مستقل جرى التحقق منه بشريًا. استخدام مصدر صوت بشري عام لا يُعفي من مراجعة الترخيص وشروط المتحدثين، ولا يجوز تسجيل موافقة لم تحدث.


## مراجعة الاستماع النوعية بعد تشغيل Whisper

بعد إتمام run_synthetic_speech_smoke.py بنجاح، أنشئ ورقة مراجعة محلية من التقرير والـmanifest نفسيهما:

~~~powershell
$report = Get-ChildItem (Join-Path $layalDataRoot "evaluation\whisper-synthetic-smoke") -Filter "whisper-synthetic-smoke-*.json" |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if (-not $report) { throw "No synthetic Whisper report was found." }

$reviewCsv = Join-Path $smokeRoot "whisper-synthetic-human-review.csv"
python .\tools\evaluation\export_synthetic_speech_review.py `
    --manifest (Join-Path $smokeRoot "synthetic-smoke-manifest.json") `
    --report $report.FullName `
    --output-csv $reviewCsv
if ($LASTEXITCODE -ne 0) { throw "Synthetic speech review-sheet export failed." }
~~~

ينشئ ذلك CSV بترميز UTF-8 BOM وملف تعليمات عربي. يحتوي CSV على مسارات WAV النسبية، واسم مولّد TTS، وprompt التوليد باعتباره سياقًا فقط، وتفريغ Whisper، وخانات فارغة للمراجعة النوعية.

استمع إلى كل ملف WAV محليًا، وسجّل وضوح الصوت والملاحظات والنص الذي سمعته عند الحاجة. لا تعتبر prompt مرجعًا ذهبيًا ولا تعتبر النص الذي تكتبه في ورقة المراجعة transcript معتمدًا. هذه مراجعة نوعية لصوت TTS الاصطناعي وليست قياس WER/CER أو بديلًا عن جمع كلام بشري بموافقة صريحة.

يعيد مشغّل Whisper الآن التحقق من بصمات أرشيف Whisper والنموذج والملف التنفيذي قبل كل عينة. عند فشل التحقق أو تغيّر أي بصمة، يتوقف عن تشغيل العينات المتبقية ويسجلها كعينات محجوبة.
