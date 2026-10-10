#!/usr/bin/env python3
"""Summarize committed NuGet locks and locally restored package license metadata."""
import json
import os
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
CACHE = Path(os.environ.get("NUGET_PACKAGES", Path.home() / ".nuget/packages"))
packages = {}
for lock in list((ROOT / "src").glob("*/packages.lock.json")) + list((ROOT / "tests").glob("*/packages.lock.json")) + list((ROOT / "tools").glob("*/packages.lock.json")):
    for framework, entries in json.loads(lock.read_text())["dependencies"].items():
        for name, dependency in entries.items():
            if dependency["type"] == "Project":
                continue
            version = dependency["resolved"]
            item = packages.setdefault((name.lower(), version), {
                "id": name, "version": version, "usedBy": [], "license": None,
                "source": None, "packageUrl": None if name == "VNO2.Shared" else f"https://www.nuget.org/packages/{name}/{version}",
                "contentHash": dependency.get("contentHash"),
            })
            use = lock.relative_to(ROOT).as_posix() + ":" + framework
            if use not in item["usedBy"]:
                item["usedBy"].append(use)
            spec = CACHE / name.lower() / version / (name.lower() + ".nuspec")
            if spec.is_file():
                metadata = ET.parse(spec).getroot()
                for element in metadata.iter():
                    tag = element.tag.rsplit("}", 1)[-1]
                    if tag == "license": item["license"] = {"type": element.get("type"), "value": element.text}
                    if tag == "repository": item["source"] = element.attrib
output = {"scope": "Current resolved NuGet graph; see lock files for exact framework resolution", "packages": [packages[key] for key in sorted(packages)]}
(ROOT / "docs/dependency-inventory.json").write_text(json.dumps(output, indent=2) + "\n")
print(f"Recorded {len(packages)} resolved package/version pairs")
