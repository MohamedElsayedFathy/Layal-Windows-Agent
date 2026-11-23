# مراجعة أمان GitHub Actions وسلسلة CI/CD — Layal

تاريخ المراجعة: 23 نوفمبر 2026  
النسخة الأساسية التي تمت مراجعتها: 8243d55933b7eae44792648209acd09890f33d2e  
النطاق: ملفات GitHub Actions الأربعة القائمة، إعداد Dependabot، بوابة توقيع التوزيع، وحماية main.

## نتائج تدقيق الحالة الأساسية

- كل workflows الأربعة كانت تعلن صلاحيات GITHUB_TOKEN على مستوى workflow كـ contents: read. وظيفة Gitleaks وحدها تطلب pull-requests: read لقراءة بيانات Pull Request.
- كل مراجع Actions الخارجية كانت مثبتة على commit SHA كامل بطول 40 خانة، بما في ذلك checkout وsetup-dotnet وsetup-python وupload-artifact وGitleaks.
- لا يوجد trigger من نوع pull_request_target أو workflow_run في workflows التي تمت مراجعتها. أحداث Pull Request تستخدم pull_request العادي بصلاحيات القراءة المعلنة.
- مرجع GITHUB_TOKEN الصريح الوحيد خارج أسرار التوقيع هو خطوة Gitleaks؛ وتعطيل تعليقات Gitleaks (GITLEAKS_ENABLE_COMMENTS: false) قائم.
- أسرار الشهادة الثلاثة لا تظهر إلا في خطوة بناء الإصدار الموقّع. تلك الخطوة مشروطة بوسم إصدار v* وبنجاح بوابة القبول/الجاهزية، كما يتحقق release guard من أن commit الوسم سلف موجود في تاريخ main.
- Dependabot مضبوط لتحديث NuGet وGitHub Actions أسبوعيًا، دون دمج تلقائي.
- حماية main مفعّلة على GitHub عند التحقق من هذه المراجعة.

## التحسينات البرمجية في هذه المرحلة

1. كل خطوة actions/checkout تضبط persist-credentials: false؛ يظل checkout قادرًا على جلب المستودع، لكنه لا يترك رمز GitHub محفوظًا في إعدادات Git المحلية للخطوات اللاحقة.
2. أضيفت مجموعات concurrency لكل workflow حتى تلغي تشغيلات PR/branch الأقدم عند وصول تعديل أحدث. تشغيلات وسوم الإصدار لا تُلغى بهذا الشرط.
3. أضيف مدقق tools/validate_github_workflows.py واختباراته. يتحقق المدقق من صلاحيات القراءة الافتراضية، وتثبيت Actions على SHAs كاملة، وعدم حفظ بيانات اعتماد checkout، ومنع triggers المميزة، وحدود مدة الاحتفاظ بالـ artifacts، وحصر أسرار التوقيع في خطوة tag المصرح بها.
4. أضيف سير عمل CodeQL لتحليل C# على Windows عند الدفع إلى main، وبجدولة أسبوعية، أو تشغيل يدوي. لا يعمل على Pull Requests لأن خطوة رفع نتائج CodeQL تتطلب security-events: write؛ تجنب إعطاء هذه الصلاحية لكود PR غير المدموج. يظل تحليل CodeQL فحصًا إرشاديًا على main وليس شرط دمج PR في هذه المرحلة.

## الإعدادات الإدارية التي لا يمكن إثباتها من ملفات Git

محتوى ملفات workflow لا يكشف كل إعدادات المستودع. لا يمكن لهذه المراجعة تأكيد القيم الفعلية لسياسة GitHub Actions العامة أو وجود أسرار/بيئات أو حمايتها؛ يجب التحقق يدويًا من إعدادات المستودع:

1. Settings → Actions → General → Workflow permissions: اجعل الافتراضي Read repository contents and packages permissions. عطّل Allow GitHub Actions to create and approve pull requests ما لم توجد حاجة تشغيلية موثقة.
2. Fork pull request workflows: أبقِ إرسال أسرار المستودع وكتابات GITHUB_TOKEN إلى workflows الآتية من forks معطّلين. لا تستخدم pull_request_target لتشغيل كود مصدر PR.
3. Settings → Environments: لا توجد في هذا التغيير بيئة توقيع معتمدة. الخطوة المستقبلية الموصى بها هي فصل التوقيع إلى job مستقل ببيئة release-signing، ونقل أسرار الشهادة إليها، وتقييد النشر إلى وسوم الإصدار/المراجع المعتمدة، وطلب موافقة تشغيل قبل كشف أسرار التوقيع. مجرد إنشاء Environment بلا قواعد حماية لا يضيف موافقة فعلية.
4. Settings → Secrets and variables → Actions: لا تضع ملفات PFX أو كلمات المرور أو Base64 في Git أو artifacts أو Issues. لا يمكن قراءة قيم الأسرار أو التحقق منها من المراجعة البرمجية، ولا ينبغي طباعتها أثناء التشخيص.
5. Settings → Code security and analysis: بعد أول تشغيل CodeQL، راجع صفحة Security → Code scanning للتأكد أن تحليل C# اكتمل، وتعامل مع التنبيهات وفق شدتها قبل الإصدار.

## حدود المراجعة

- لم تتغير قيم أسرار GitHub ولم تُقرأ أو تُنقل أي قيمة سرية.
- لم تُغيّر إعدادات Actions الإدارية أو إعدادات Environments من خلال هذه المراجعة.
- فحص workflows لا يثبت أن كل اعتماد خارجي آمن؛ SHA يثبت ثبات المرجع فقط، ويجب مراجعة تحديثات Dependabot قبل دمجها.
- CodeQL لا يغني عن مراجعة التصميم والاختبارات الديناميكية ومراجعة بشرية للتعامل مع العمليات المحلية والملفات وبيانات المستخدم.
- نتائج CodeQL لا تُضاف إلى شروط حماية main الحالية في هذه المرحلة. بعد تقييم الضوضاء والاستقرار يمكن ضم check إلزامي لاحقًا بطريقة لا تعطي PR token صلاحيات كتابة أمنية.

## المراجع الفنية

- GitHub CodeQL Action releases — v4.39.4 (20 Nov 2026): https://github.com/github/codeql-action/releases/tag/v4.39.4
- GitHub Docs: CodeQL build options for compiled languages: https://docs.github.com/en/code-security/reference/code-scanning/codeql/build-options-for-compiled-languages
- GitHub Docs: CodeQL for compiled languages: https://docs.github.com/en/code-security/how-tos/find-and-fix-code-vulnerabilities/manage-your-configuration/codeql-for-compiled-languages
