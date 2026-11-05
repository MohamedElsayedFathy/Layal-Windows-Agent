# إشعار خصوصية تكامل Gmail في Layal

*هذا الإشعار يصف معالجة بيانات Google Workspace/Gmail داخل تكامل Gmail في Layal. يجب على مالك التطبيق مراجعته وتحديث بيانات هوية المطوّر ووسائل التواصل وروابط النشر، ثم نشر نسخة عامة على نطاق يملكه ويتحقق منه قبل إتاحة OAuth لمستخدمين خارجيين. هذا المستند ليس شهادة تحقق أو موافقة من Google.*

## العربية

### البيانات التي تصل إليها Layal

عند ربط حساب Gmail، تطلب Layal صلاحية OAuth واحدة هي `gmail.readonly`. تتيح هذه الصلاحية قراءة الرسائل وأجسامها وبياناتها ومرفقاتها. لا تطلب Layal صلاحيات إرسال البريد أو حذفه أو تعديله أو إدارة إعداداته.

لا تجلب Layal الرسائل إلا بعد فتح نافذة Gmail أو تنفيذ المستخدم لبحث/تحديث. تُجلب الرسائل المعروضة وتفاصيل الرسالة المختارة فقط، وفق حدود المعاينة الموثقة في [دليل Gmail](GMAIL_OAUTH_SETUP_AR.md).

### الغرض من الاستخدام

تُستخدم بيانات Gmail لتوفير واجهة البريد التي طلبها المستخدم، وعرض الرسائل وتحويل HTML إلى نص آمن، وتقديم معاينات محلية محدودة للنصوص والمستندات والصور لأغراض الإنتاجية. لا تستخدم Layal بيانات Gmail للإعلانات، ولا تبيعها، ولا تستخدمها داخل Layal لتدريب أو تحسين نماذج ذكاء اصطناعي عامة. تلتزم معالجة بيانات Google Workspace بسياسة بيانات مستخدمي Google ومتطلبات Limited Use.

### التخزين والأمان

- يحفظ Refresh Token محليًا ضمن ملف محمي بواسطة Windows DPAPI لحساب Windows الحالي. لا يُحفظ Client Secret؛ تطبيق Desktop يستخدم Client ID عامًا وتدفق OAuth مع PKCE وstate.
- يبقى Access Token في ذاكرة العملية ويُجدد عند الحاجة. تُجرى طلبات Gmail عبر HTTPS.
- لا تحفظ Layal مرفقات Gmail في Workspace أو Downloads. المعاينة المحلية للمستندات والصور تستخدم ملفًا مؤقتًا خاصًا، ثم تحذفه بعد الاستخراج أو عند حدوث خطأ/إلغاء.
- تُصفّر بايتات المرفقات بعد المعالجة أو رفض الحجم. لا تشغّل Layal HTML أو JavaScript أو ماكرو أو ملفات تنفيذية من البريد.
- قد يُحفظ النص الذي يرسله المستخدم إلى الشات ضمن سجل المحادثة المحلي المشفر بواسطة DPAPI، وفق حدود سياسة [الاحتفاظ المحلي](LOCAL_DATA_RETENTION_AR.md).

### المشاركة مع مزود ذكاء اصطناعي

لا تُرسل الرسالة أو مرفقاتها إلى مزود AI عند فتح Gmail أو البحث أو المعاينة. إذا اختار المستخدم «إضافة إلى مسودة الشات» أو «تجهيز طلب تلخيص»، تظهر نافذة موافقة قبل نسخ نص الرسالة ومعاينات المرفقات إلى مسودة الشات. يظل ذلك محليًا ولا يستدعي AI.

لا ينتقل المحتوى إلى مزود AI إلا بعد أن يراجع المستخدم مسودة الشات ويضغط «إرسال» بنفسه. عندها يتلقى مزود AI المحدد في إعدادات Layal المحتوى المرسل، ويخضع المحتوى لسياسة ذلك المزود ومعالجته واحتفاظه. يجب على المستخدم مراجعة شروط المزود والتأكد من التزامه بقيود Google، وخاصة عدم استخدام بيانات Gmail لتدريب أو تحسين نماذج AI عامة. لا تضمن Layal سلوك مزود خارجي لا تملكه.

### الاحتفاظ والتحكم والحذف

- «فصل Gmail محليًا» يحذف Refresh Token من هذا الجهاز ويمسح Access Token من ذاكرة العملية؛ ولا يسحب التفويض من حساب Google.
- لسحب التفويض من Google، افتح [أذونات حساب Google](https://myaccount.google.com/permissions) وأزل وصول Layal.
- تُدار الرسائل التي أرسلها المستخدم إلى الشات وفق سياسة الاحتفاظ المحلية. مسح سجل المحادثات يحذف النسخ المحلية المحفوظة ضمن Layal، لكنه لا يستطيع حذف نسخ سبق إرسالها إلى مزود AI أو أنظمة النسخ الاحتياطي/الاحتفاظ التابعة له.
- لا تُنشئ Layal أرشيفًا دائمًا لمرفقات Gmail؛ ملفات المعاينة المؤقتة تُحذف عقب الاستخراج.

### سياسة Google

تلتزم Layal باستخدام ونقل المعلومات التي تتلقاها من نطاقات Google Workspace وفق [سياسة بيانات مستخدمي Google Workspace](https://developers.google.com/workspace/workspace-api-user-data-developer-policy)، بما فيها متطلبات **Limited Use**. لا يُسمح بأي نقل لبيانات Gmail إلا لتقديم ميزة ظاهرة وبارزة للمستخدم وبموافقته، ولا يجوز استخدام تلك البيانات لتدريب أو تحسين نماذج عامة خارج الاستخدام المخصص للمستخدم الذي تسمح به سياسة Google.

### التواصل

قبل النشر العام، يجب على مالك Layal إضافة اسم الجهة المسؤولة وبريد دعم/خصوصية صالح إلى هذا القسم ونشر المستند على نطاق عام يملكه ويتحقق منه:

- الجهة المسؤولة: **يُستكمل بواسطة مالك التطبيق قبل النشر**.
- بريد الخصوصية والدعم: **يُستكمل بواسطة مالك التطبيق قبل النشر**.
- تاريخ آخر مراجعة: **يُحدّث عند كل تعديل جوهري**.

---

## English

### Data accessed

When a user connects Gmail, Layal requests only the OAuth scope `gmail.readonly`. This scope can read message content, metadata, headers, and attachments. Layal does not request Gmail scopes for sending, deleting, modifying, or managing mailbox settings.

Gmail data is retrieved only after the user opens the inbox or runs a search/refresh. Layal fetches the listed messages and the details of the selected message, subject to the local preview limits in the [Gmail integration guide](GMAIL_OAUTH_SETUP_AR.md).

### Purpose and use

Gmail data is used to provide the user-requested email interface, display messages, convert HTML to safe plain text, and create bounded local previews of supported text, document, and image attachments for productivity features. Layal does not sell Gmail data, use it for advertising, or use it within Layal to train or improve general AI models. Layal's use of Google Workspace data adheres to Google's User Data Policy, including the Limited Use requirements.

### Storage and security

- The OAuth refresh token is stored locally in a Windows DPAPI-protected file bound to the current Windows user. Layal does not store a client secret; the desktop OAuth flow uses a public client ID, PKCE, and state validation.
- Access tokens remain in process memory and are refreshed when needed. Gmail API requests use HTTPS.
- Gmail attachments are not saved into the Layal workspace or Downloads. Local document/image preview uses a private temporary file that is deleted after extraction, including failure/cancellation paths.
- Attachment byte buffers are zeroed after processing or size rejection. Layal does not execute email HTML, JavaScript, macros, or executable attachments.
- Message text that the user sends to chat may be stored in the local DPAPI-protected conversation history under the [local retention policy](LOCAL_DATA_RETENTION_AR.md).

### Sharing with an AI provider

Opening Gmail, searching, or previewing a message does not send its content to an AI provider. When the user chooses to add email text and attachment previews to a chat draft, Layal displays a separate consent prompt. The content remains local in the draft and no AI request is made at this stage.

Content is transferred to the AI provider configured in Layal only after the user reviews the draft and presses Send. That provider then processes and retains the submitted content under its own terms. Users must review those terms and confirm the provider complies with Google's restrictions, especially the prohibition on using Gmail data to train or improve general AI models. Layal cannot guarantee the conduct of an external provider it does not control.

### Retention, controls, and deletion

- Disconnecting Gmail locally deletes the refresh token on this device and clears the in-memory access token; it does not revoke Google's grant.
- To revoke Google access, remove Layal from [Google Account permissions](https://myaccount.google.com/permissions).
- Email content sent to chat is subject to Layal's local conversation retention policy. Clearing local chat history removes the locally stored copy within Layal, but cannot delete copies already sent to an AI provider or retained by that provider or its backup systems.
- Layal does not create a permanent Gmail-attachment archive; temporary preview files are deleted after extraction.

### Google policy commitment

Layal's use and transfer of information received from Google Workspace scopes adheres to the [Google Workspace User Data and Developer Policy](https://developers.google.com/workspace/workspace-api-user-data-developer-policy), including the **Limited Use** requirements. Gmail data is transferred only to provide user-facing features that are visible and prominent in Layal and only with the user's consent. It is not used to train or improve general AI models beyond the user's personalized model for the appropriate user-facing feature as permitted by Google's policy.

### Contact and publication details

Before public distribution, the application owner must complete this section and publish this policy on a public, verified domain owned by the application owner:

- Responsible entity: **to be completed by the application owner before release**.
- Privacy/support contact: **to be completed by the application owner before release**.
- Last reviewed: **update after each material policy change**.
