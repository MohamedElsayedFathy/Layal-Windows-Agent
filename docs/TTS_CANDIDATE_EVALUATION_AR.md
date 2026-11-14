# تقييم ومقارنة محركات النطق (TTS) في ليال

**الحالة:** أداة التقييم جاهزة؛ لا توجد نتائج صوتية فعلية مسجلة بعد. لا يعني نجاح الأداة أن أي مرشح صوتي اجتاز القبول.

## الهدف والنطاق

هذا المسار يقارن Piper الحالي بالمرشحين الذين تمت مراجعتهم في VOICE_PERSONA_TARGET_AR.md، دون تعديل محرك الإنتاج أو استبدال أي ملفات صوت مثبتة. مجموعة الاختبار الموحدة هي tests/TestAssets/Speech/voice-persona-evaluation.json (18 عبارة: مصرية، فصحى، إنجليزية، ومختلطة) عبر أنماط الإلقاء الثمانية.

المرشحون المسجلون للبحث:

- **Piper baseline:** خط الأساس الحالي، بصوت ar_JO-kareem-medium وen_US-lessac-medium. يستخدم للمقارنة ولا يحقق وحده الشخصية المستهدفة.
- **KemeTone:** مرشح مصري قاهري فقط مبدئيًا. لا نفترض أنه يدعم الإنجليزية أو المزج. لا يُعتمد قبل تثبيت revision وبصمة الملفات والتحقق من حقوق الصوت الأصلي المذكور في بطاقة النموذج.
- **VoiceTut-TTS:** مرشح للمصرية والإنجليزية المختلطة. يجب قياس MSA والأداء على Windows/CPU/GPU، ومراجعة مصدر وحقوق الأصوات المدمجة. لا يُفعّل voice cloning.

لا تعتبر أسماء المرشحين أو التراخيص العامة موافقة على تنزيل أو تشغيل أو توزيع أوزان بعينها. قبل إنتاج أي عينة: راجع بطاقة النموذج عند revision ثابت، وسجّل commit/version غير متحرك، وSHA-256 لكل ملف وزن، ورابط دليل الترخيص، ودليل حقوق المتحدث/الصوت. أي حالة pending تمنع اعتماد المرشح للإنتاج.

## مبدأ الأمان

الأداة tools/evaluation/score_tts_candidates.py لا تتصل بالشبكة، ولا تنزّل النماذج، ولا تبدأ عمليات inference، ولا ترفع التسجيلات أو ملفات WAV. هي تقرأ فقط ملفات خرجها المستخدم محليًا. تنزيل المرشحين أو ربطهم بتطبيق Layal ليس جزءًا من هذه الأداة ولا من هذا التغيير.

لا تحفظ أي تسجيل لميكروفون مستخدم ضمن مجموعة TTS. عينات TTS هنا هي خرج صناعي من النصوص الثابتة. استخدم معرفات مجهولة للمراجعين (raterId) ولا تضع أسماء أو بيانات شخصية في manifest أو ملاحظاته.

## إعداد تجربة قابلة لإعادة الإنتاج

أنشئ مجلد تجربة مستقلًا خارج ملفات الإنتاج، مثل:

~~~text
artifacts/tts-evaluation/run-001/
  run.json
  samples/
    piper_baseline__egy-01__natural.wav
    kemetone__egy-01__natural.wav
~~~

أنشئ run.json بالشكل التالي، وكرّر إدخالات samples لكل عبارة/محرك/نمط مدعوم. القيم الصفرية وبصمات الأصفار في المثال عناصر نائبة شكلية؛ استبدلها بقياسات وبصمات حقيقية قبل التشغيل:

~~~json
{
  "schemaVersion": 1,
  "runId": "win-device-01-2026-11-14",
  "device": {
    "os": "Windows 11",
    "cpu": "اكتب طراز المعالج",
    "ramMb": 0,
    "gpu": "اكتب اسم البطاقة أو null"
  },
  "engines": [
    {
      "id": "candidate-id",
      "displayName": "اسم المحرك والإصدار",
      "sourceUrl": "https://example.org/model-card",
      "revision": "اكتب commit SHA أو إصدارًا ثابتًا",
      "revisionIsImmutable": false,
      "speakerId": "معرف صوت ثابت لا يتغير بين اللغات",
      "licenseStatus": "pending",
      "licenseEvidenceUrl": null,
      "speakerRightsStatus": "pending",
      "speakerRightsEvidenceUrl": null,
      "voiceCloningEnabled": false,
      "modelSha256": ["0000000000000000000000000000000000000000000000000000000000000000"],
      "supportedLanguages": ["EgyptianArabic"],
      "supportedStyles": ["natural", "gentle"]
    }
  ],
  "samples": [
    {
      "caseId": "egy-01",
      "engineId": "candidate-id",
      "style": "natural",
      "audioPath": "samples/candidate-id__egy-01__natural.wav",
      "audioSha256": "0000000000000000000000000000000000000000000000000000000000000000",
      "renderTimeMs": 0,
      "timeToFirstAudioMs": 0,
      "ratings": []
    }
  ]
}
~~~

يجب أن تكون WAV بصيغة PCM أحادية القناة و16-bit، ويمكن الاحتفاظ بمعدل العينة الأصلي لكل محرك وتسجيله كما هو. ضع مسارات نسبية داخل مجلد التجربة فقط؛ الأداة ترفض المسارات المطلقة ومسارات الخروج من مجلد التجربة، وتتحقق من SHA-256 لملف WAV قبل قياسه.

حقول تقييم البشر (كل منها من 1 إلى 5):

- naturalness: طبيعية النطق.
- intelligibility: سهولة الفهم.
- softness: النعومة والدفء.
- accentFit: ملاءمة اللهجة للعبارة.
- identityConsistency: ثبات هوية المتحدثة بين اللغات.
- prosodyFit: ملاءمة التنغيم والوقفات للمعنى والنمط.
- pronunciationAccuracy: دقة مخارج الحروف والأسماء والأرقام.

أضف تقييم كل مراجع في عنصر مستقل، بمعرف مجهول مختلف. الأداة تعرض متوسط كل بُعد على حدة؛ لا تصنع درجة إجمالية اعتباطية تخفي ضعفًا في جانب معين.

## تشغيل الأداة

من جذر المستودع:

~~~powershell
python tools/evaluation/score_tts_candidates.py artifacts/tts-evaluation/run-001/run.json --validate-only
python tools/evaluation/score_tts_candidates.py artifacts/tts-evaluation/run-001/run.json --report-json artifacts/tts-evaluation/run-001/report.json
python tools/evaluation/score_tts_candidates.py artifacts/tts-evaluation/run-001/run.json --strict-complete
~~~

- فحص manifest وWAV لا يعني أن نموذجًا قد تم تشغيله أو أنه جيد.
- renderTimeMs هو زمن إنشاء الملف كاملًا، وtimeToFirstAudioMs هو زمن أول عينة مسموعة وفق قياس أداة التوليد. تُحسب RTF بقسمة زمن التوليد على مدة الصوت؛ الأقل أسرع.
- RMS/Peak/Clipping مؤشرات سلامة إشارة وليست مقياسًا لجمال الصوت أو صحة نطقه.
- تغطية الاختبار تحسب فقط للتركيبات التي أعلن المحرك دعمها. المفقود يخفض التغطية ولا يعامل كدرجة صوت صفرية.
- --strict-complete يرجع 3 ما دامت هناك حالات/أنماط ناقصة.
- لا يصبح أي محرك مؤهلًا للإنتاج إلا بعد revision ثابت، وبصمات أوزان، ودليل ترخيص وحقوق صوت موثقين، وتعطيل استنساخ الصوت، واكتمال مصفوفة الاختبار. هذا الشرط الآلي لا يغني عن المراجعة البشرية.

## ما لا تثبته هذه المرحلة

لا يوجد حتى الآن أي ملف WAV مرجعي أو MOS/تقييم بشري أو قياس RTF على جهاز المستخدم. لا يتم استبدال Piper في التطبيق في هذه المرحلة. بعد إعداد مخرجات المرشحين وتشغيل المقارنة، تُراجع عينات المصرية والفصحى والإنجليزية والمختلطة بالاستماع، ثم يصدر قرار قبول منفصل قبل بناء تكامل إنتاجي.
