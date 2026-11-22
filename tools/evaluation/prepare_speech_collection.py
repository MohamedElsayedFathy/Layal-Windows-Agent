#!/usr/bin/env python3
"""Prepare a local workspace for collecting consented Layal speech samples.

This tool copies transcript prompts only. It does not record audio, assert consent,
approve references, or upload any data. The output directory must be outside the
repository so recordings and signed consent records cannot be committed accidentally.
"""
from __future__ import annotations

import argparse
import csv
import json
import os
import re
import shutil
import sys
import tempfile
from collections import Counter
from pathlib import Path
from typing import Any

REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_SEED_MANIFEST = REPO_ROOT / "tests" / "TestAssets" / "Speech" / "speech-reference-set.json"
SUPPORTED_LOCALES = {"ar-EG", "ar", "en", "mixed"}
CASE_ID_PATTERN = re.compile(r"^[a-z0-9][a-z0-9-]{2,40}$")

PACKAGE_README = """# مساحة جمع بيانات الكلام — Layal

هذه مساحة عمل محلية تحتوي على نصوص طلب التسجيل وملف manifest ومجلد فارغ للصوت.
**لم يُسجَّل أي صوت، ولم تُسجَّل أي موافقة أو مراجعة بشرية عند إنشاء الحزمة.**

## الملفات

- `speech-reference-set.json`: نسخة عمل من حالات النص المرجعي.
- `recording-sheet.csv`: ورقة prompts قابلة للفتح في Excel (UTF-8 مع BOM).
- `audio/`: ضع فيها ملفات WAV بعد الحصول على موافقة المتحدث.
- `CONSENT_FORM_AR.md`: نموذج موافقة يجب استكمال بيانات المشروع وسياسة الاحتفاظ به قبل الجمع.

## خطوات الجمع

1. اقرأ نموذج الموافقة مع المتحدث، وأجب عن أسئلته، ولا تبدأ التسجيل حتى يوافق طوعًا ويوقّع/يوثق النموذج.
2. استكمل جهة المشروع ووسيلة التواصل ومكان التخزين وموعد/مدة الحذف في نموذج الموافقة قبل استخدامه.
3. احتفظ بنموذج الموافقة المعرّف بالهوية في تخزين محلي آمن منفصل عن ملفات الصوت وmanifest. لا تضع الاسم أو التوقيع أو بيانات الاتصال في Git أو manifest.
   
المجلد الناتج يرث صلاحيات الوصول من المجلد الأب؛ الأداة لا تغيّر Windows ACL ولا تشفّر الملفات. اختر موقعًا لا يصل إليه إلا الأشخاص المخولون، ولا تستخدم مجلد شبكة أو مجلدًا مشتركًا.
4. أنشئ رمزًا مستعارًا مثل `spk-001` للمتحدث ورمزًا مثل `rev-001` لمراجع النص. لا تستخدم اسمًا أو بريدًا أو رقم هاتف.
5. سجّل جملة واحدة لكل ملف باسم `<case_id>.wav` داخل `audio/`. لا تسجل بيانات حساسة أو كلمات مرور أو أسماء/عناوين أو معلومات تخص أشخاصًا آخرين.
6. احتفظ بالصوت الأصلي محليًا، ثم حدّث الحالة المقابلة في manifest فقط بعد التسجيل الفعلي: `recordingStatus=recorded` و`audioAsset=audio/<case_id>.wav` و`speakerId`، وأضف `consent` بنطاق `research-evaluation` ووقت الموافقة الفعلي.
7. اجعل `referenceReview.status=pending` أولًا. استمع للصوت واكتب ما قيل فعلًا، ثم اعتمد النص بشريًا وأضف `reviewerId` المستعار ووقت المراجعة الحقيقي قبل تشغيل ASR.
8. شغّل فاحص manifest وWhisper benchmark من نسخة المستودع. لا تنسخ الصوت أو نموذج الموافقة أو تقارير التفريغ إلى المستودع أو PR.

## شروط ملف الصوت

PCM WAV، قناة واحدة mono، 16,000 Hz، 16-bit، مدة أكبر من صفر وحتى 120 ثانية، وحجم لا يتجاوز 64 MiB. يجب أن يكون مسار الصوت نسبيًا داخل `audio/`.

## حدود هذه الحزمة

النصوص الحالية prompts مقروءة وليست عينة محادثة عفوية. لا تستنتج منها دقة للمحادثة الطبيعية أو اكتشاف نهاية الكلام أو تنفيذ الأوامر. اجمع بيانات إضافية منفصلة لهذه الظواهر بعد مراجعة الموافقة وبروتوكولها. لا تستخدم هذه الحزمة للتدريب أو الاستنساخ الصوتي أو النشر أو المشاركة الخارجية؛ نطاقها المقصود هو التقييم البحثي المحلي فقط.
"""

CONSENT_FORM = """# نموذج موافقة طوعية لتسجيل الكلام — تقييم Layal المحلي

> **يجب إكمال جميع الخانات المتعلقة بالمشروع والتخزين والاحتفاظ قبل طلب الموافقة.** هذا نموذج تشغيلي أولي وليس استشارة قانونية؛ راجعه بما يتوافق مع القوانين والسياسات المنطبقة على مشروعك.

## بيانات الجهة المسؤولة عن الجمع

- اسم الجهة/مسؤول المشروع: __________________________________
- وسيلة تواصل للاستفسارات أو سحب الموافقة: ____________________
- الغرض المحدد: تقييم جودة تحويل الكلام إلى نص (ASR) محليًا في مشروع Layal.
- مكان حفظ التسجيلات: ________________________________________
- من يمكنه الوصول إليها: _____________________________________
- مدة الاحتفاظ وتاريخ الحذف النهائي: __________________________
- طريقة طلب سحب الموافقة: ____________________________________

**لا تبدأ التسجيل إذا كانت أي من خانات الجهة أو التواصل أو التخزين أو الوصول أو مدة الاحتفاظ أو السحب غير مكتملة أو غير مفهومة للمتطوع.**

## ما الذي أوافق عليه؟

إذا وافقت، سيتم تسجيل الجمل التي أقرأها/أنطقها وحفظ ملفات WAV محليًا لاختبار محرك التعرف على الكلام في Layal. يُستخدم الصوت والنص المرجعي لهذا التقييم المحدد فقط، وتُجرى المعالجة على جهاز محلي دون رفع التسجيل إلى خدمة ASR سحابية ضمن هذا الإجراء.

هذه الموافقة **لا** تشمل:
- تدريب نموذج أو تحسين أوزانه أو fine-tuning.
- استنساخ صوتي أو تقليد هوية صوتي أو إنشاء صوت اصطناعي يشبهني.
- النشر أو العرض العام أو مشاركة التسجيلات مع طرف خارجي.
- استخدام التسجيل في منتج/غرض آخر أو تقييم آخر غير موضح هنا.

سيُربط الملف بمعرّف مستعار مثل `spk-001` داخل manifest. لا تُكتب هويتي أو بيانات اتصالي في manifest أو Git. ويُحفظ أي نموذج موافقة يحمل بيانات هويتي منفصلًا في تخزين آمن ومقيّد الوصول.

المشاركة اختيارية. يمكنني رفض أي جملة أو إيقاف المشاركة وطلب سحب الموافقة باستخدام وسيلة التواصل أعلاه. يوضح مسؤول المشروع ما يمكن حذفه وكيفية تنفيذ الحذف وفق سياسة الاحتفاظ التي تم شرحها لي.

## إقرار المتطوع

أؤكد أن الغرض وطريقة التخزين ومدة الاحتفاظ ووسيلة التواصل وسحب الموافقة قد شُرحت لي، وأتيحت لي فرصة طرح الأسئلة، وأوافق طوعًا على **التقييم البحثي المحلي لتحويل الكلام إلى نص فقط**.

- الاسم/التوقيع في النسخة المحلية الآمنة فقط: __________________
- التاريخ والوقت والمنطقة الزمنية: ____________________________
- رمز المتحدث المستعار المستخدم في manifest: __________________
- توقيع/تأكيد مسؤول جمع الموافقة: ____________________________

☐ موافق على النطاق المحدد أعلاه.  
☐ غير موافق — لا يتم التسجيل ولا إنشاء حالة مسجلة.

**لا تُدخل الاسم أو التوقيع أو بيانات التواصل في manifest أو CSV أو أي ملف داخل المستودع.** احتفظ بهذه النسخة الموقعة محليًا في مكان منفصل وآمن. لا تضعها في GitHub أو PR.
"""


def _is_relative_to(path: Path, parent: Path) -> bool:
    try:
        path.relative_to(parent)
        return True
    except ValueError:
        return False


def _load_seed_manifest(seed_manifest: Path) -> dict[str, Any]:
    if not seed_manifest.is_file():
        raise ValueError(f"Seed manifest does not exist: {seed_manifest}")
    try:
        payload = json.loads(seed_manifest.read_text(encoding="utf-8-sig"))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise ValueError(f"Cannot read seed manifest: {error}") from error

    if not isinstance(payload, dict) or payload.get("schemaVersion") != 1:
        raise ValueError("Seed manifest must be a JSON object with schemaVersion=1.")
    allowed_root_keys = {"schemaVersion", "description", "recordingStatus", "cases"}
    unexpected_root_keys = set(payload) - allowed_root_keys
    if unexpected_root_keys:
        raise ValueError("Seed manifest contains unsupported root metadata fields.")
    if payload.get("recordingStatus") != "not-recorded":
        raise ValueError("Seed manifest root recordingStatus must be not-recorded.")

    cases = payload.get("cases")
    if not isinstance(cases, list) or not cases:
        raise ValueError("Seed manifest must contain a non-empty cases array.")

    allowed_case_keys = {
        "id", "locale", "referenceText", "audioAsset", "recordingStatus",
        "speakerId", "consentRequired", "hypotheses",
    }
    sanitized_cases: list[dict[str, Any]] = []
    seen_ids: set[str] = set()
    for index, case in enumerate(cases, start=1):
        prefix = f"cases[{index}]"
        if not isinstance(case, dict):
            raise ValueError(f"{prefix} must be an object.")
        unexpected_case_keys = set(case) - allowed_case_keys
        if unexpected_case_keys:
            raise ValueError(f"{prefix} contains unsupported metadata fields.")
        if case.get("consentRequired") is not True:
            raise ValueError(f"{prefix} must require explicit consent.")
        case_id = case.get("id")
        if not isinstance(case_id, str) or not CASE_ID_PATTERN.fullmatch(case_id):
            raise ValueError(f"{prefix}.id must be a safe, non-identifying token.")
        if case_id in seen_ids:
            raise ValueError(f"Duplicate case id: {case_id}")
        seen_ids.add(case_id)
        if case.get("locale") not in SUPPORTED_LOCALES:
            raise ValueError(f"{prefix}.locale is not supported.")
        if not isinstance(case.get("referenceText"), str) or not case["referenceText"].strip():
            raise ValueError(f"{prefix}.referenceText must be non-empty text.")
        if case.get("recordingStatus", "not-recorded") != "not-recorded":
            raise ValueError(f"{prefix} is already marked recorded; only transcript-only seed cases are accepted.")
        if case.get("audioAsset") is not None or case.get("speakerId") is not None:
            raise ValueError(f"{prefix} must not already reference audio or a speaker.")
        if case.get("consent") is not None or case.get("referenceReview") is not None:
            raise ValueError(f"{prefix} must not contain consent or reference-review metadata.")
        hypotheses = case.get("hypotheses", {})
        if not isinstance(hypotheses, dict) or hypotheses:
            raise ValueError(f"{prefix} must not contain ASR hypotheses.")

        sanitized_cases.append({key: case.get(key) for key in allowed_case_keys if key in case})

    return {
        "schemaVersion": 1,
        "description": "Transcript-only speech collection prompts; no recordings, consent, or approved references.",
        "recordingStatus": "not-recorded",
        "cases": sanitized_cases,
    }


def _write_text(path: Path, content: str) -> None:
    path.write_text(content.rstrip() + "\n", encoding="utf-8", newline="\n")


def _write_recording_sheet(path: Path, cases: list[dict[str, Any]]) -> None:
    columns = ("order", "case_id", "locale", "reference_text", "suggested_audio_filename")
    with path.open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=columns, extrasaction="raise")
        writer.writeheader()
        for order, case in enumerate(cases, start=1):
            writer.writerow({
                "order": order,
                "case_id": case["id"],
                "locale": case["locale"],
                "reference_text": case["referenceText"],
                "suggested_audio_filename": f'{case["id"]}.wav',
            })


def prepare_workspace(
    seed_manifest: Path,
    output_dir: Path,
    repository_root: Path = REPO_ROOT,
) -> dict[str, Any]:
    seed_manifest = seed_manifest.expanduser().resolve()
    target = output_dir.expanduser().resolve()
    repo_root = repository_root.expanduser().resolve()

    if target == repo_root or _is_relative_to(target, repo_root):
        raise ValueError("Output directory must be outside the Git repository.")
    if target.exists():
        raise FileExistsError(f"Output directory already exists; choose a new empty path: {target}")

    payload = _load_seed_manifest(seed_manifest)
    target.parent.mkdir(parents=True, exist_ok=True)
    staging = Path(tempfile.mkdtemp(prefix=f".{target.name}.staging-", dir=target.parent))
    try:
        (staging / "audio").mkdir()
        (staging / "speech-reference-set.json").write_text(
            json.dumps(payload, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
            newline="\n",
        )
        _write_recording_sheet(staging / "recording-sheet.csv", payload["cases"])
        _write_text(staging / "README_AR.md", PACKAGE_README)
        _write_text(staging / "CONSENT_FORM_AR.md", CONSENT_FORM)

        if target.exists():
            raise FileExistsError(f"Output directory appeared during preparation: {target}")
        os.replace(staging, target)
    except Exception:
        if staging.exists():
            shutil.rmtree(staging, ignore_errors=True)
        raise

    locales = Counter(case["locale"] for case in payload["cases"])
    return {
        "output_dir": target,
        "case_count": len(payload["cases"]),
        "locale_counts": dict(sorted(locales.items())),
        "audio_dir": target / "audio",
    }


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--output-dir",
        type=Path,
        required=True,
        help="New local collection directory outside the repository; it must not already exist.",
    )
    parser.add_argument(
        "--seed-manifest",
        type=Path,
        default=DEFAULT_SEED_MANIFEST,
        help="Transcript-only starter manifest (defaults to Layal's 30-case speech set).",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    try:
        summary = prepare_workspace(args.seed_manifest, args.output_dir)
    except (OSError, ValueError) as error:
        print(f"Speech collection workspace blocked: {error}", file=sys.stderr)
        return 2

    print(f"Workspace: {summary['output_dir']}")
    print(f"Cases: {summary['case_count']}")
    print("Locale counts: " + ", ".join(f"{locale}={count}" for locale, count in summary["locale_counts"].items()))
    print(f"Audio folder: {summary['audio_dir']}")
    print("No audio was recorded; no consent was asserted; no reference was approved.")
    print("Next: review CONSENT_FORM_AR.md, collect consent, record WAV files, then update the local manifest.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
