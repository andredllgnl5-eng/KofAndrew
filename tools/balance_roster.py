from __future__ import annotations

import argparse
import json
import re
import shutil
import statistics
from pathlib import Path


TEXT_EXTENSIONS = {".cns", ".st", ".zss", ".txt"}
DAMAGE_RE = re.compile(r"^(?P<prefix>\s*(?:damage|hitdamage)\s*=\s*)(?P<hit>\d+)(?P<rest>\s*(?:,\s*(?P<guard>\d+))?.*)$", re.I)
CEIL_DAMAGE_RE = re.compile(r"^(?P<prefix>\s*(?:damage|hitdamage)\s*=\s*(?:\(\s*)?ceil\(\s*)(?P<hit>\d+)(?P<rest>\s*\).*)$", re.I)
STAT_RE = {
    "life": re.compile(r"^(?P<prefix>\s*life\s*=\s*)[-+]?\d+(?P<rest>\s*(?:;.*)?)$", re.I),
    "attack": re.compile(r"^(?P<prefix>\s*attack\s*=\s*)[-+]?\d+(?P<rest>\s*(?:;.*)?)$", re.I),
    "defence": re.compile(r"^(?P<prefix>\s*defence\s*=\s*)[-+]?\d+(?P<rest>\s*(?:;.*)?)$", re.I),
}


def character_entries(select_def: Path) -> list[str]:
    result, active = [], False
    for raw in select_def.read_text(encoding="utf-8", errors="ignore").splitlines():
        line = raw.split(";", 1)[0].strip()
        if line.lower() == "[characters]":
            active = True
            continue
        if active and line.startswith("["):
            break
        if active and line and line.lower() != "randomselect":
            result.append(line.split(",", 1)[0].strip())
    return result


def resolve_def(chars: Path, entry: str) -> Path:
    direct = chars / entry
    candidates = [direct, direct.with_suffix(".def")]
    if direct.is_dir():
        candidates.insert(0, direct / f"{direct.name}.def")
        candidates.extend(sorted(direct.glob("*.def")))
    for candidate in candidates:
        if candidate.is_file():
            return candidate
    raise FileNotFoundError(entry)


def text_files(folder: Path) -> list[Path]:
    return [p for p in folder.rglob("*") if p.is_file() and p.suffix.lower() in TEXT_EXTENSIONS]


def read_source(path: Path) -> tuple[str, str]:
    data = path.read_bytes()
    try:
        return data.decode("utf-8"), "utf-8"
    except UnicodeDecodeError:
        # latin-1 preserva cada byte dos personagens antigos (Shift-JIS/ANSI).
        return data.decode("latin-1"), "latin-1"


def percentile(values: list[int], fraction: float) -> float:
    if not values:
        return 0.0
    ordered = sorted(values)
    index = (len(ordered) - 1) * fraction
    low, high = int(index), min(len(ordered) - 1, int(index) + 1)
    weight = index - low
    return ordered[low] * (1 - weight) + ordered[high] * weight


def normalized_damage(value: int, factor: float) -> int:
    # Preserva multi-hits baixos e aproxima o dano forte do padrão KOF.
    if value <= 12:
        return value
    adjusted = round(value * factor)
    if value <= 35:
        return max(12, min(38, adjusted))
    if value <= 90:
        return max(36, min(92, adjusted))
    if value <= 180:
        return max(85, min(165, adjusted))
    return max(150, min(300, adjusted))


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("game", type=Path)
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--backup", type=Path)
    args = parser.parse_args()
    game = args.game.resolve()
    chars = game / "chars"
    roster = []
    for entry in character_entries(game / "data" / "select.def"):
        char_def = resolve_def(chars, entry)
        files = text_files(char_def.parent)
        damages = []
        for path in files:
            source, _ = read_source(path)
            for line in source.splitlines():
                match = DAMAGE_RE.match(line) or CEIL_DAMAGE_RE.match(line)
                if match and int(match.group("hit")) > 0:
                    damages.append(int(match.group("hit")))
        roster.append({"entry": entry, "def": char_def, "files": files, "damages": damages})

    valid_p75 = [percentile(item["damages"], .75) for item in roster if item["damages"]]
    target_p75 = statistics.median(valid_p75) if valid_p75 else 70.0
    report = {"targetP75": target_p75, "characters": []}
    for item in roster:
        values = item["damages"]
        p75 = percentile(values, .75)
        factor = 1.0 if p75 <= 0 else max(.82, min(1.18, target_p75 / p75))
        changed_lines = 0
        changed_files = 0
        stat_changes = 0
        for path in item["files"]:
            raw, source_encoding = read_source(path)
            output = []
            in_data = False
            local_changes = 0
            for line in raw.splitlines(keepends=True):
                body = line.rstrip("\r\n")
                ending = line[len(body):]
                section = re.match(r"^\s*\[([^]]+)\]", body)
                if section:
                    in_data = section.group(1).strip().lower() == "data"
                replaced = body
                if in_data:
                    for stat, value in (("life", 1000), ("attack", 100), ("defence", 100)):
                        match = STAT_RE[stat].match(body)
                        if match:
                            replaced = f"{match.group('prefix')}{value}{match.group('rest')}"
                            if replaced != body:
                                stat_changes += 1
                                local_changes += 1
                            break
                match = DAMAGE_RE.match(replaced) or CEIL_DAMAGE_RE.match(replaced)
                if match:
                    old_hit = int(match.group("hit"))
                    new_hit = normalized_damage(old_hit, factor)
                    guard = match.groupdict().get("guard")
                    new_guard = None if guard is None else min(int(guard), max(0, round(new_hit * .25)))
                    rest = match.group("rest")
                    if guard is not None:
                        rest = re.sub(r"^(\s*,\s*)\d+", lambda m: m.group(1) + str(new_guard), rest, count=1)
                    replaced = f"{match.group('prefix')}{new_hit}{rest}"
                    if replaced != body:
                        changed_lines += 1
                        local_changes += 1
                output.append(replaced + ending)
            if args.apply and local_changes:
                if args.backup:
                    relative = path.relative_to(chars)
                    target = args.backup / relative
                    target.parent.mkdir(parents=True, exist_ok=True)
                    if not target.exists():
                        shutil.copy2(path, target)
                with path.open("w", encoding=source_encoding, newline="") as stream:
                    stream.write("".join(output))
                changed_files += 1
        report["characters"].append({
            "entry": item["entry"], "damageEntries": len(values),
            "p50Before": percentile(values, .5), "p75Before": p75,
            "p90Before": percentile(values, .9), "maxBefore": max(values, default=0),
            "factor": round(factor, 4), "changedDamageLines": changed_lines,
            "changedStatLines": stat_changes, "changedFiles": changed_files,
        })
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"characters": len(roster), "targetP75": target_p75,
                      "damageEntries": sum(len(x["damages"]) for x in roster)}, ensure_ascii=False))


if __name__ == "__main__":
    main()
