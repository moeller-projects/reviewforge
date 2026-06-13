from __future__ import annotations
from pathlib import Path
import json, re
from typing import Any


def write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True); path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n")


def read_json(path: Path) -> Any:
    return json.loads(path.read_text())


def changed_files(files: list[str]) -> list[dict[str, Any]]:
    ext_lang = {"ts":"TypeScript","tsx":"TypeScript","js":"JavaScript","jsx":"JavaScript","mjs":"JavaScript","cjs":"JavaScript","py":"Python","rb":"Ruby","go":"Go","java":"Java","cs":"C#","cpp":"C++","cc":"C++","c":"C","h":"C","rs":"Rust","kt":"Kotlin","swift":"Swift","php":"PHP","sh":"Shell","bash":"Shell","ps1":"PowerShell","psm1":"PowerShell","psd1":"PowerShell","tf":"HCL","hcl":"HCL","json":"JSON","yaml":"YAML","yml":"YAML","md":"Markdown","html":"HTML","css":"CSS","scss":"SCSS","sql":"SQL"}
    test_file = re.compile(r"(\.(test|spec)\.[^.]+$|_test\.[^.]+$|Test\.[^.]+$)"); test_path = re.compile(r"(^|/)(test|tests|__tests__|spec|specs)/")
    return [{"file": f, "language": ext_lang.get(f.rsplit('.',1)[-1].lower() if '.' in f else '', "Other"), "isTest": bool(test_file.search(f) or test_path.search(f))} for f in files]
