# حماية فرع main — Layal

## الهدف

فرض الدمج من خلال Pull Request إلى `main` بدل الدفع المباشر، وربط الدمج بنتائج فحوص Windows والأسرار وتبعيات NuGet. هذا إعداد GitHub على مستوى المستودع؛ وجود السكربت في Git لا يفعّله تلقائيًا.

## السياسة التي يطبقها السكربت

يشغّل `tools/Configure-LayalMainProtection.ps1` واجهة GitHub الرسمية بعد التحقق من تسجيل الدخول وصلاحية Admin ومن أن `main` هو الفرع الافتراضي. قبل أي تغيير يعرض السياسة ويعمل افتراضيًا في وضع المعاينة فقط.

- اشتراط Pull Request مع نجاح فحوص `build` و`Scan Git commit range for exposed secrets` و`Audit NuGet advisories and license inventory`.
- اشتراط أن يكون فرع Pull Request محدثًا مع `main` عند الدمج.
- تطبيق الحماية على المسؤولين أيضًا.
- منع Force Push وحذف فرع `main`.
- اشتراط حل محادثات المراجعة وتاريخ خطي للـ commits.
- لا يفرض موافقة بشرية افتراضيًا، حتى لا يُقفل المستودع على مالك واحد لا يستطيع اعتماد Pull Request الخاص به. ما زال Pull Request وجميع فحوص CI إلزاميًا.

إذا أصبح هناك مراجع آخر موثوق لديه صلاحية المراجعة، يمكن طلب موافقة واحدة صريحة:

```powershell
.\tools\Configure-LayalMainProtection.ps1 -RequiredApprovingReviews 1
```

في هذا الوضع يشترط السكربت أيضًا أن تكون الموافقة على آخر دفعة تغييرات من شخص غير صاحبها.

## خطوات التفعيل من Windows

1. ثبّت GitHub CLI إذا لم يكن موجودًا: [cli.github.com](https://cli.github.com/).
2. سجّل الدخول بحساب يملك صلاحية Admin على المستودع:

```powershell
gh auth login --hostname github.com
gh auth status --hostname github.com
```

3. بعد تحديث فرع `main` إلى النسخة التي تحتوي السكربت، شغّل المعاينة:

```powershell
.\tools\Configure-LayalMainProtection.ps1
```

4. راجع أسماء فحوص CI والسياسة المعروضة. إذا كانت صحيحة، شغّل:

```powershell
.\tools\Configure-LayalMainProtection.ps1 -Apply
```

5. اكتب النص التالي حرفيًا عند طلب التأكيد:

```text
MohamedElsayedFathy/Layal-Windows-Agent/main
```

6. السكربت يقرأ الإعدادات مرة أخرى بعد التحديث ويفشل إذا لم تطابق السياسة المطلوبة. بعد ذلك يمكن التحقق يدويًا من [إعدادات حماية main](https://github.com/MohamedElsayedFathy/Layal-Windows-Agent/settings/branches).

## ملاحظات تشغيلية

- لم يتم تفعيل الحماية بمجرد إضافة هذا المستند أو تعديل السكربت؛ يلزم تشغيله محليًا بحساب Admin.
- بعد التفعيل، استخدم فروعًا مستقلة وPull Requests لكل تغيير، وانتظر نجاح الفحوص الإلزامية قبل الدمج.
- لأن التاريخ الخطي مطلوب، استخدم Squash أو Rebase merge، ولا تستخدم Merge commits.
- لا تشغّل أي أمر تنظيف للملفات المحلية كجزء من إعداد حماية الفرع؛ لا يحتاج هذا الإجراء إلى لمس مجلد `Layal/` أو بياناته.
