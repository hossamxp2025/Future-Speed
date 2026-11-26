using System.Globalization;
using FutureSpeed.Core.Models;

namespace FutureSpeed.Core.Utils;

/// <summary>
/// The human wording for a <see cref="FailureReason"/>: one sentence saying what happened and
/// one saying what to do about it, in Arabic or English.
///
/// It lives in the core because both the on-screen failure list and the written report must
/// say exactly the same thing; duplicating these sentences in the UI layer would let them drift.
/// </summary>
public static class FailureText
{
    private static readonly Dictionary<FailureReason, string> ArabicWhat = new()
    {
        [FailureReason.InUse] = "الملف مفتوح في برنامج آخر ويمنع قراءته",
        [FailureReason.AccessDenied] = "لا توجد صلاحية لقراءة الملف أو للكتابة في الوجهة",
        [FailureReason.NotFound] = "الملف أو مجلده لم يعد موجوداً",
        [FailureReason.PathTooLong] = "مسار الوجهة أطول مما يسمح به نظام الملفات",
        [FailureReason.DiskFull] = "لا توجد مساحة كافية في قرص الوجهة",
        [FailureReason.DestinationUnavailable] = "قرص الوجهة غير متاح (مُزال أو غير جاهز)",
        [FailureReason.SourceUnavailable] = "قرص المصدر غير متاح (مُزال أو غير جاهز)",
        [FailureReason.ReadOnlyTarget] = "الوجهة محمية من الكتابة",
        [FailureReason.NetworkError] = "موقع الشبكة توقف عن الاستجابة",
        [FailureReason.InvalidName] = "اسم الملف يحتوي محارف لا يقبلها نظام ملفات الوجهة",
        [FailureReason.VerificationMismatch] = "بصمة SHA-256 للنسخة لا تطابق المصدر",
        [FailureReason.DeviceError] = "خطأ في القراءة أو الكتابة على مستوى القرص (قطاع تالف محتمل)",
        [FailureReason.SkippedByUser] = "تم تخطيه بطلب المستخدم",
        [FailureReason.Unknown] = "سبب غير معروف"
    };

    private static readonly Dictionary<FailureReason, string> ArabicFix = new()
    {
        [FailureReason.InUse] = "أغلق البرنامج الذي يستخدم الملف ثم اضغط «إعادة محاولة الفاشل».",
        [FailureReason.AccessDenied] = "شغّل البرنامج كمسؤول، أو عدّل صلاحيات الملف أو المجلد، ثم أعد المحاولة.",
        [FailureReason.NotFound] = "تأكد من وجود الملف في مصدره، فقد يكون حُذف أو نُقل أثناء العملية.",
        [FailureReason.PathTooLong] = "اختر وجهة أقصر في المسار، أو قصّر أسماء المجلدات الداخلية.",
        [FailureReason.DiskFull] = "أفرغ مساحة في قرص الوجهة أو اختر قرصاً آخر ثم أعد المحاولة.",
        [FailureReason.DestinationUnavailable] = "أعد توصيل قرص الوجهة وتأكد من ظهوره في «هذا الكمبيوتر» ثم أعد المحاولة.",
        [FailureReason.SourceUnavailable] = "أعد توصيل قرص المصدر ثم أعد المحاولة.",
        [FailureReason.ReadOnlyTarget] = "أزل الحماية من الكتابة عن القرص أو اختر وجهة أخرى.",
        [FailureReason.NetworkError] = "تحقّق من الاتصال بالشبكة أو بالسيرفر ثم أعد المحاولة.",
        [FailureReason.InvalidName] = "أعد تسمية الملف بإزالة المحارف غير المسموحة (مثل : * ? \" < > |).",
        [FailureReason.VerificationMismatch] = "أعد النسخ؛ إن تكرر الاختلاف فافحص سلامة القرص أو الكابل.",
        [FailureReason.DeviceError] = "افحص القرص بأداة chkdsk أو جرّب منفذاً أو كابلاً آخر.",
        [FailureReason.SkippedByUser] = "أعد العملية إن كنت تريد نقل هذا الملف.",
        [FailureReason.Unknown] = "راجع الرسالة التقنية وملف السجل لمزيد من التفاصيل."
    };

    private static readonly Dictionary<FailureReason, string> EnglishWhat = new()
    {
        [FailureReason.InUse] = "The file is open in another program",
        [FailureReason.AccessDenied] = "No permission to read the source or write the destination",
        [FailureReason.NotFound] = "The file or its folder no longer exists",
        [FailureReason.PathTooLong] = "The destination path is longer than the file system allows",
        [FailureReason.DiskFull] = "Not enough free space on the destination volume",
        [FailureReason.DestinationUnavailable] = "The destination drive is unavailable (removed or not ready)",
        [FailureReason.SourceUnavailable] = "The source drive is unavailable (removed or not ready)",
        [FailureReason.ReadOnlyTarget] = "The destination is write protected",
        [FailureReason.NetworkError] = "The network location stopped responding",
        [FailureReason.InvalidName] = "The name contains characters the destination file system rejects",
        [FailureReason.VerificationMismatch] = "The SHA-256 of the copy does not match the source",
        [FailureReason.DeviceError] = "A device read/write error (possibly a damaged sector)",
        [FailureReason.SkippedByUser] = "Skipped on request",
        [FailureReason.Unknown] = "Unknown cause"
    };

    private static readonly Dictionary<FailureReason, string> EnglishFix = new()
    {
        [FailureReason.InUse] = "Close the program holding the file, then use \"Retry failed\".",
        [FailureReason.AccessDenied] = "Run as administrator or adjust the file/folder permissions, then retry.",
        [FailureReason.NotFound] = "Check the source: the file may have been deleted or moved during the transfer.",
        [FailureReason.PathTooLong] = "Pick a shorter destination path, or shorten the inner folder names.",
        [FailureReason.DiskFull] = "Free space on the destination or choose another drive, then retry.",
        [FailureReason.DestinationUnavailable] = "Reconnect the destination drive and make sure Windows lists it, then retry.",
        [FailureReason.SourceUnavailable] = "Reconnect the source drive, then retry.",
        [FailureReason.ReadOnlyTarget] = "Remove the write protection or choose a different destination.",
        [FailureReason.NetworkError] = "Check the network or server connection, then retry.",
        [FailureReason.InvalidName] = "Rename the file without the characters : * ? \" < > |.",
        [FailureReason.VerificationMismatch] = "Copy it again; if it keeps differing, check the disk or the cable.",
        [FailureReason.DeviceError] = "Run chkdsk on the volume, or try another port or cable.",
        [FailureReason.SkippedByUser] = "Run the transfer again if you do want this file.",
        [FailureReason.Unknown] = "See the technical message and the log file for details."
    };

    /// <summary>What happened, in one sentence.</summary>
    public static string What(FailureReason reason, CultureInfo? culture = null)
        => Lookup(IsArabic(culture) ? ArabicWhat : EnglishWhat, reason);

    /// <summary>What the user can do about it, in one sentence.</summary>
    public static string Fix(FailureReason reason, CultureInfo? culture = null)
        => Lookup(IsArabic(culture) ? ArabicFix : EnglishFix, reason);

    /// <summary>True when the reason is worth retrying once the user has acted on it.</summary>
    public static bool IsRetryable(FailureReason reason) => reason is
        FailureReason.InUse or FailureReason.AccessDenied or FailureReason.DiskFull or
        FailureReason.DestinationUnavailable or FailureReason.SourceUnavailable or
        FailureReason.NetworkError or FailureReason.VerificationMismatch or
        FailureReason.ReadOnlyTarget or FailureReason.DeviceError or FailureReason.Unknown;

    private static bool IsArabic(CultureInfo? culture)
        => (culture ?? CultureInfo.CurrentUICulture).TwoLetterISOLanguageName
            .Equals("ar", StringComparison.OrdinalIgnoreCase);

    private static string Lookup(Dictionary<FailureReason, string> table, FailureReason reason)
        => table.TryGetValue(reason, out var text)
            ? text
            : table[FailureReason.Unknown];
}
