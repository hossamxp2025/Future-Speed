using System.Globalization;
using System.Net;
using System.Text;
using FutureSpeed.Core.Abstractions;
using FutureSpeed.Core.Models;
using FutureSpeed.Core.Utils;

namespace FutureSpeed.Core.Services;

/// <summary>
/// Builds the printable transfer report: one self-contained HTML file, no external
/// stylesheet or script, so it opens in any browser and can be emailed as-is.
///
/// Failures come first and always carry the full source path, the intended destination,
/// the cause and a suggested action — the point of the report is that nothing that did
/// not transfer stays invisible.
/// </summary>
public static class ReportHtmlBuilder
{
    public static string Build(TransferResult result, CultureInfo culture)
    {
        var arabic = culture.TwoLetterISOLanguageName.Equals("ar", StringComparison.OrdinalIgnoreCase);
        var t = arabic ? Arabic : English;
        var summary = FailureSummary.FromItems(result.Items, result.Job.Scan?.Problems);

        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html>");
        sb.AppendLine($"<html lang=\"{(arabic ? "ar" : "en")}\" dir=\"{(arabic ? "rtl" : "ltr")}\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"utf-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.AppendLine($"<title>{E(t["title"])} — {E(result.Job.Name)}</title>");
        sb.AppendLine(Css);
        sb.AppendLine("</head><body>");

        // ---- header -------------------------------------------------------------
        sb.AppendLine("<header class=\"hero\">");
        sb.AppendLine("  <div class=\"brand\">Future Speed <span>File Transfer</span></div>");
        sb.AppendLine($"  <h1>{E(t["title"])}</h1>");
        sb.AppendLine($"  <p class=\"sub\">{E(result.Job.Name)}</p>");
        sb.AppendLine("</header>");
        sb.AppendLine("<main>");

        // ---- verdict ------------------------------------------------------------
        var (verdictClass, verdictText) = Verdict(result, summary, t, culture);
        sb.AppendLine($"<section class=\"verdict {verdictClass}\"><strong>{E(verdictText)}</strong></section>");

        // ---- summary ------------------------------------------------------------
        var average = result.Duration.TotalSeconds > 0 ? result.TransferredBytes / result.Duration.TotalSeconds : 0;

        sb.AppendLine("<section class=\"cards\">");
        Card(sb, t["mode"], result.Job.Mode == TransferMode.Move ? t["move"] : t["copy"]);
        Card(sb, t["filesTotal"], result.Items.Count.ToString(culture));
        Card(sb, t["filesDone"], result.CopiedFiles.ToString(culture), "ok");
        Card(sb, t["filesSkipped"], result.SkippedFiles.ToString(culture));
        Card(sb, t["filesFailed"], summary.Count.ToString(culture), summary.HasFailures ? "bad" : null);
        Card(sb, t["transferred"], ByteFormatter.FormatSize(result.TransferredBytes, culture));
        Card(sb, t["missing"], ByteFormatter.FormatSize(summary.FailedBytes, culture), summary.FailedBytes > 0 ? "bad" : null);
        Card(sb, t["duration"], ByteFormatter.FormatDuration(result.Duration, culture));
        Card(sb, t["average"], ByteFormatter.FormatSpeed(average, culture));
        Card(sb, t["verify"], result.Job.Options.VerifyWithSha256 ? t["on"] : t["off"]);
        sb.AppendLine("</section>");

        // ---- job facts ----------------------------------------------------------
        sb.AppendLine("<section class=\"panel\">");
        sb.AppendLine($"<h2>{E(t["jobDetails"])}</h2><dl>");
        Row(sb, t["state"], StateText(result.FinalState, t));
        Row(sb, t["destination"], result.Job.DestinationRoot, mono: true);
        Row(sb, t["sources"], string.Join(" · ", result.Job.Sources), mono: true);
        Row(sb, t["started"], result.Job.StartedAt?.ToString("yyyy-MM-dd HH:mm:ss", culture) ?? "—");
        Row(sb, t["finished"], result.Job.FinishedAt?.ToString("yyyy-MM-dd HH:mm:ss", culture) ?? "—");
        Row(sb, t["jobId"], result.Job.Id, mono: true);
        sb.AppendLine("</dl></section>");

        // ---- failures -----------------------------------------------------------
        if (summary.HasFailures)
        {
            sb.AppendLine("<section class=\"panel bad-panel\">");
            sb.AppendLine($"<h2>{E(t["failures"])} <span class=\"count\">{summary.Count}</span></h2>");
            sb.AppendLine($"<p class=\"note\">{E(t["failuresNote"])}</p>");

            sb.AppendLine("<ul class=\"chips\">");
            foreach (var (reason, count) in summary.ByReason)
                sb.AppendLine($"<li><span class=\"n\">{count}</span> {E(FailureText.What(reason, culture))}</li>");
            sb.AppendLine("</ul>");

            sb.AppendLine("<table><thead><tr>");
            sb.AppendLine($"<th>#</th><th>{E(t["file"])}</th><th>{E(t["sourcePath"])}</th><th>{E(t["destinationPath"])}</th>"
                        + $"<th>{E(t["size"])}</th><th>{E(t["reason"])}</th><th>{E(t["code"])}</th><th>{E(t["action"])}</th>");
            sb.AppendLine("</tr></thead><tbody>");

            var index = 1;
            foreach (var failure in summary.Failures)
            {
                sb.AppendLine("<tr>");
                sb.AppendLine($"<td>{index++}</td>");
                sb.AppendLine($"<td>{E(failure.FileName)}</td>");
                sb.AppendLine($"<td class=\"mono\">{E(failure.SourcePath)}</td>");
                sb.AppendLine($"<td class=\"mono\">{E(failure.DestinationPath ?? "—")}</td>");
                sb.AppendLine($"<td>{E(ByteFormatter.FormatSize(failure.Size, culture))}</td>");
                // The OS message is Latin text in an Arabic page, so it gets its own LTR block
                // instead of being reordered into nonsense by the bidi algorithm.
                sb.AppendLine($"<td>{E(FailureText.What(failure.Reason, culture))}"
                            + $"<div class=\"tech\">{E(OriginText(failure.Origin, t))}"
                            + (string.IsNullOrWhiteSpace(failure.Message)
                                ? string.Empty
                                : $"<span class=\"msg\" dir=\"ltr\">{E(failure.Message!)}</span>")
                            + "</div></td>");
                sb.AppendLine($"<td class=\"mono code\">{E(failure.Code)}</td>");
                sb.AppendLine($"<td>{E(FailureText.Fix(failure.Reason, culture))}</td>");
                sb.AppendLine("</tr>");
            }

            sb.AppendLine("</tbody></table></section>");
        }

        // ---- skipped ------------------------------------------------------------
        var skipped = result.Items.Where(i => i.Outcome == FileOutcome.Skipped).ToList();
        if (skipped.Count > 0)
        {
            sb.AppendLine("<section class=\"panel\">");
            sb.AppendLine($"<h2>{E(t["skipped"])} <span class=\"count neutral\">{skipped.Count}</span></h2>");
            sb.AppendLine($"<p class=\"note\">{E(t["skippedNote"])}</p><ul class=\"paths\">");
            foreach (var item in skipped)
                sb.AppendLine($"<li class=\"mono\">{E(item.SourcePath)}</li>");
            sb.AppendLine("</ul></section>");
        }

        // ---- everything that did transfer ---------------------------------------
        var done = result.Items.Where(i => i.Outcome is FileOutcome.Copied or FileOutcome.Moved
            or FileOutcome.Renamed or FileOutcome.Overwritten).ToList();

        sb.AppendLine("<section class=\"panel\">");
        sb.AppendLine($"<details><summary>{E(t["transferredList"])} ({done.Count})</summary>");
        sb.AppendLine("<table class=\"compact\"><thead><tr>");
        sb.AppendLine($"<th>{E(t["sourcePath"])}</th><th>{E(t["destinationPath"])}</th><th>{E(t["size"])}</th><th>SHA-256</th>");
        sb.AppendLine("</tr></thead><tbody>");
        foreach (var item in done)
        {
            sb.AppendLine("<tr>"
                + $"<td class=\"mono\">{E(item.SourcePath)}</td>"
                + $"<td class=\"mono\">{E(item.EffectiveDestinationPath ?? item.DestinationPath)}</td>"
                + $"<td>{E(ByteFormatter.FormatSize(item.Size, culture))}</td>"
                + $"<td class=\"mono small\">{E(item.DestinationHash ?? "—")}</td></tr>");
        }
        sb.AppendLine("</tbody></table></details></section>");

        sb.AppendLine($"<footer>{E(t["generated"])} {E(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", culture))} · Future Speed File Transfer</footer>");
        sb.AppendLine("</main></body></html>");

        return sb.ToString();
    }

    private static (string Class, string Text) Verdict(
        TransferResult result,
        FailureSummary summary,
        IReadOnlyDictionary<string, string> t,
        CultureInfo culture)
        => result.FinalState switch
        {
            JobState.Canceled => ("warn", string.Format(culture, t["verdictCanceled"], summary.Count)),
            JobState.Failed => ("bad", string.Format(culture, t["verdictFailed"], summary.Count)),
            _ when summary.HasFailures => ("bad", string.Format(culture, t["verdictPartial"], summary.Count)),
            _ => ("ok", t["verdictOk"])
        };

    private static string StateText(JobState state, IReadOnlyDictionary<string, string> t)
        => t.TryGetValue("state_" + state, out var text) ? text : state.ToString();

    private static string OriginText(FailureOrigin origin, IReadOnlyDictionary<string, string> t)
        => t["origin_" + origin];

    private static void Card(StringBuilder sb, string label, string value, string? tone = null)
        => sb.AppendLine($"<div class=\"card {tone}\"><span class=\"label\">{E(label)}</span><span class=\"value\">{E(value)}</span></div>");

    private static void Row(StringBuilder sb, string label, string value, bool mono = false)
        => sb.AppendLine($"<dt>{E(label)}</dt><dd class=\"{(mono ? "mono" : string.Empty)}\">{E(value)}</dd>");

    private static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private const string Css = """
<style>
  :root{
    --bg:#f5f7fa; --card:#ffffff; --line:#e3e8ef; --text:#1b2430; --muted:#6b7684;
    --blue:#1f6feb; --green:#19a974; --red:#d7263d; --amber:#b58105;
  }
  *{box-sizing:border-box}
  body{margin:0;background:var(--bg);color:var(--text);
       font-family:"Segoe UI",Tahoma,system-ui,sans-serif;font-size:14px;line-height:1.6}
  .hero{background:linear-gradient(135deg,#1f6feb,#19a974);color:#fff;padding:28px 32px}
  .hero .brand{font-size:13px;letter-spacing:.12em;text-transform:uppercase;opacity:.9}
  .hero .brand span{font-weight:300}
  .hero h1{margin:6px 0 2px;font-size:26px}
  .hero .sub{margin:0;opacity:.92}
  main{max-width:1180px;margin:0 auto;padding:24px 32px 56px}
  .verdict{padding:14px 18px;border-radius:10px;margin-bottom:20px;border:1px solid var(--line);background:var(--card)}
  .verdict.ok{border-inline-start:5px solid var(--green)}
  .verdict.bad{border-inline-start:5px solid var(--red)}
  .verdict.warn{border-inline-start:5px solid var(--amber)}
  .cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:12px;margin-bottom:22px}
  .card{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:12px 14px;display:flex;flex-direction:column;gap:4px}
  .card .label{color:var(--muted);font-size:12px}
  .card .value{font-size:18px;font-weight:600}
  .card.ok .value{color:var(--green)} .card.bad .value{color:var(--red)}
  .panel{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:18px 20px;margin-bottom:20px}
  .panel.bad-panel{border-color:#f3c3cb}
  .panel h2{margin:0 0 10px;font-size:17px}
  .count{display:inline-block;background:var(--red);color:#fff;border-radius:999px;padding:1px 10px;font-size:13px;vertical-align:middle}
  .count.neutral{background:var(--muted)}
  .note{color:var(--muted);margin:0 0 14px}
  dl{display:grid;grid-template-columns:max-content 1fr;gap:6px 18px;margin:0}
  dt{color:var(--muted)} dd{margin:0;word-break:break-all}
  .chips{list-style:none;display:flex;flex-wrap:wrap;gap:8px;padding:0;margin:0 0 16px}
  .chips li{background:#fdf0f2;border:1px solid #f3c3cb;border-radius:999px;padding:4px 12px}
  .chips .n{font-weight:700;color:var(--red)}
  table{width:100%;border-collapse:collapse;font-size:13px}
  th,td{padding:8px 10px;border-bottom:1px solid var(--line);text-align:start;vertical-align:top}
  th{background:#f0f3f8;color:var(--muted);font-weight:600;white-space:nowrap}
  tbody tr:hover{background:#f8fafc}
  /* Paths and codes are Latin text inside an Arabic page: pin them to LTR so the
     segments of a path keep their order instead of being reordered by the bidi algorithm. */
  .mono{font-family:Consolas,"Courier New",monospace;word-break:break-all;direction:ltr;text-align:left}
  td.code{white-space:nowrap}
  .small{font-size:11px} .tech{color:var(--muted);font-size:12px;margin-top:3px}
  .tech .msg{display:block;direction:ltr;text-align:left;margin-top:2px}
  .paths{margin:0;padding-inline-start:18px} .paths li{word-break:break-all}
  details summary{cursor:pointer;font-weight:600;margin-bottom:10px}
  table.compact td{padding:5px 8px}
  footer{color:var(--muted);font-size:12px;text-align:center;padding-top:10px}
  @media print{body{background:#fff}.panel,.card{break-inside:avoid}details>summary{display:none}}
</style>
""";

    private static readonly Dictionary<string, string> Arabic = new()
    {
        ["title"] = "تقرير عملية النقل",
        ["mode"] = "الوضع",
        ["copy"] = "نسخ",
        ["move"] = "نقل",
        ["filesTotal"] = "إجمالي الملفات",
        ["filesDone"] = "نُقلت بنجاح",
        ["filesSkipped"] = "تم تخطيها",
        ["filesFailed"] = "فشلت",
        ["transferred"] = "الحجم المنقول",
        ["missing"] = "حجم لم يُنقل",
        ["duration"] = "المدة",
        ["average"] = "متوسط السرعة",
        ["verify"] = "التحقق SHA-256",
        ["on"] = "مُفعّل",
        ["off"] = "غير مُفعّل",
        ["jobDetails"] = "تفاصيل العملية",
        ["state"] = "الحالة",
        ["destination"] = "الوجهة",
        ["sources"] = "المصادر",
        ["started"] = "البداية",
        ["finished"] = "النهاية",
        ["jobId"] = "معرّف العملية",
        ["failures"] = "ملفات لم تُنقل",
        ["failuresNote"] = "كل صف يحمل المسار الكامل للملف، وسبب الفشل، والإجراء المقترح لإتمامه.",
        ["file"] = "الملف",
        ["sourcePath"] = "المسار في المصدر",
        ["destinationPath"] = "المسار في الوجهة",
        ["size"] = "الحجم",
        ["reason"] = "السبب",
        ["code"] = "الكود التقني",
        ["action"] = "الإجراء المقترح",
        ["skipped"] = "ملفات تم تخطيها",
        ["skippedNote"] = "تُخطَّت بقرار سياسة التعارض أو بطلب المستخدم، وليست أخطاء.",
        ["transferredList"] = "قائمة الملفات التي نُقلت بنجاح",
        ["generated"] = "أُنشئ هذا التقرير في",
        ["verdictOk"] = "اكتملت العملية بنجاح دون أي ملف فاشل.",
        ["verdictPartial"] = "اكتملت العملية مع {0} ملف لم يُنقل — التفاصيل في جدول «ملفات لم تُنقل».",
        ["verdictFailed"] = "توقفت العملية بخطأ، و{0} ملف لم يُنقل.",
        ["verdictCanceled"] = "أُلغيت العملية بطلب المستخدم، و{0} ملف لم يُنقل. لم يُحذف أي ملف مصدر.",
        ["origin_Copy"] = "أثناء النسخ",
        ["origin_Verification"] = "أثناء التحقق",
        ["origin_Scan"] = "أثناء فحص المصدر",
        ["state_Completed"] = "مكتملة",
        ["state_CompletedWithErrors"] = "مكتملة مع أخطاء",
        ["state_Failed"] = "فاشلة",
        ["state_Canceled"] = "ملغاة"
    };

    private static readonly Dictionary<string, string> English = new()
    {
        ["title"] = "Transfer report",
        ["mode"] = "Mode",
        ["copy"] = "Copy",
        ["move"] = "Move",
        ["filesTotal"] = "Files in total",
        ["filesDone"] = "Transferred",
        ["filesSkipped"] = "Skipped",
        ["filesFailed"] = "Failed",
        ["transferred"] = "Data transferred",
        ["missing"] = "Data not transferred",
        ["duration"] = "Duration",
        ["average"] = "Average speed",
        ["verify"] = "SHA-256 verification",
        ["on"] = "enabled",
        ["off"] = "disabled",
        ["jobDetails"] = "Job details",
        ["state"] = "State",
        ["destination"] = "Destination",
        ["sources"] = "Sources",
        ["started"] = "Started",
        ["finished"] = "Finished",
        ["jobId"] = "Job id",
        ["failures"] = "Files that did not transfer",
        ["failuresNote"] = "Every row carries the full path, the cause, and what to do to finish it.",
        ["file"] = "File",
        ["sourcePath"] = "Source path",
        ["destinationPath"] = "Destination path",
        ["size"] = "Size",
        ["reason"] = "Reason",
        ["code"] = "Technical code",
        ["action"] = "Suggested action",
        ["skipped"] = "Skipped files",
        ["skippedNote"] = "Skipped by the conflict policy or on request — these are not errors.",
        ["transferredList"] = "Files transferred successfully",
        ["generated"] = "Report generated at",
        ["verdictOk"] = "The transfer finished with no failed file.",
        ["verdictPartial"] = "The transfer finished with {0} file(s) not transferred — see the failures table.",
        ["verdictFailed"] = "The transfer stopped with an error; {0} file(s) did not transfer.",
        ["verdictCanceled"] = "The transfer was cancelled; {0} file(s) did not transfer. No source file was deleted.",
        ["origin_Copy"] = "during the copy",
        ["origin_Verification"] = "during verification",
        ["origin_Scan"] = "while scanning the source",
        ["state_Completed"] = "Completed",
        ["state_CompletedWithErrors"] = "Completed with errors",
        ["state_Failed"] = "Failed",
        ["state_Canceled"] = "Canceled"
    };
}
