# Future Speed File Transfer 1.1.2

## لا يختفي ملف فاشل بصمت

كانت العمليات التي يفشل فيها ملف تنتهي بعدّاد فقط: «فاشلة: 1»، دون اسم الملف ولا سببه.
هذا الإصدار يجعل كل ملف لم يُنقل مرئياً وقابلاً للتصرف.

### الجديد
- **لوحة «ملفات لم تُنقل»** لكل عملية: اسم الملف · مساره الكامل في المصدر · مساره المقصود
  في الوجهة · الحجم · سبب الفشل بجملة مفهومة · الكود التقني (مثل `InUse (32)`) ·
  المرحلة (أثناء النسخ / التحقق / فحص المصدر) · الإجراء المقترح لإتمامه.
  تظهر في نافذة النقل المصغّرة (تبويب يفتح نفسه عند أول فشل)، وقائمة الانتظار، والسجل.
- **تقرير HTML احترافي** يُنشأ تلقائياً بعد أي عملية بها فشل ويُفتح بزر واحد: حُكم العملية،
  بطاقات ملخّص، تجميع الأسباب، جدول الملفات الفاشلة بالمسارات الكاملة، ثم قائمة ما نُقل
  بنجاح مع بصماته. صفحة مستقلة تماماً بلا ملفات خارجية — تُرسل بالبريد أو تُطبع كما هي.
- **تصنيف أسباب الفشل:** ملف مفتوح في برنامج آخر · لا صلاحية · الملف لم يعد موجوداً ·
  مسار أطول من المسموح · القرص ممتلئ · قرص الوجهة غير متاح · وسيط محمي من الكتابة ·
  خطأ شبكة · اسم غير صالح · خطأ قرص · عدم تطابق SHA-256.
- الملفات التي يتعذّر حتى **فحصها** صارت تُعرض كفشل صريح بدل أن تختفي في نتيجة الفحص.
- زرّا **نسخ القائمة** (كل المسارات والأسباب إلى الحافظة) و**فتح مجلد الملف**.
- تقارير JSON / CSV / TXT تحمل السبب والكود وعدد المحاولات والإجراء المقترح والمسار الكامل،
  وJSON صار يحمل قسم `failures` مستقلاً للمعالجة الآلية.
- إشعار النهاية يذكر عدد الملفات التي لم تُنقل.

### أُصلح
- قراءة كود Windows من داخل `HResult` كانت تفشل دائماً بسبب توسيع القناع إلى `long`،
  فكان كل فشل يُصنَّف «غير معروف».
- رسائل الخطأ كانت تعرض بادئة المسارات الطويلة الداخلية داخل نص الرسالة.
- بطاقة الفشل في لوحة العملية الحالية كانت تظهر فارغة بعد انتهاء العملية.
- التقرير التلقائي كان مرتبطاً بشاشة السجل، فلم يكن يُكتب حين يفتح المستكشف النافذة
  المصغّرة وحدها — انتقل إلى مكوّن مستقل يعمل في كل مسارات التشغيل.

### الجودة
بناء Release بـ **0 أخطاء و0 تحذيرات**، و**282 اختباراً** تمر بالكامل (237 في النواة +
45 في طبقة التطبيق)، منها اختبار يحجز ملفاً فعلياً من عملية أخرى ويتحقق من السبب والمسار،
ومن أن الأصل لا يُحذف في وضع النقل.

### التثبيت
نزّل `FutureSpeed-1.1.2-Setup.exe` وشغّله. التثبيت للمستخدم الحالي بلا صلاحيات مدير،
وتكامل مستكشف الملفات اختياري أثناء التثبيت وقابل للإلغاء من الإعدادات في أي وقت.
الترقية فوق إصدار سابق تحافظ على الإعدادات وسجل العمليات.

---

## In English

**Nothing that failed stays invisible.** Every file that could not be transferred now
appears with its full source path, its intended destination, a plain-language cause
(file open in another program, permissions, path too long, disk full, device removed,
SHA-256 mismatch…), the underlying Windows code such as `InUse (32)`, and a suggested
action. A printable, self-contained HTML report is written automatically for any run with
failures and opens with one click; JSON reports gained a dedicated `failures` section.

Fixes: the Windows error code was never read out of the `HResult` (mask widening bug), so
every failure was classified as "unknown"; internal long-path prefixes leaked into error
messages; the failure card could render empty after a job ended; and the automatic report
was tied to the history page, so it was never written when Explorer opened the compact
window alone.

Release build: 0 errors, 0 warnings, 282 tests passing.
