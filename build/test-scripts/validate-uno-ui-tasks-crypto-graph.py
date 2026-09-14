"""Check Uno.UI.Tasks restores the patched ASN.1 dependency with auditing."""
import argparse
import json
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--source-root", type=Path, default=Path(__file__).resolve().parents[2])
args = parser.parse_args()

project = "Uno.UI.Tasks"
package = "System.Formats.Asn1"
expected = "8.0.1"
assets_path = args.source_root / "src" / "SourceGenerators" / project / "obj" / "project.assets.json"
failures = []

if not assets_path.is_file():
    failures.append(f"{project}: missing restored assets")
else:
    assets = json.loads(assets_path.read_text(encoding="utf-8-sig"))
    audit = assets["project"]["restore"].get("restoreAuditProperties", {})
    if str(audit.get("enableAudit", "")).lower() != "true" or audit.get("auditMode") != "all":
        failures.append(f"{project}: NuGetAudit must remain enabled in all mode: {audit}")

    warnings = assets["project"]["restore"].get("warningProperties", {})
    if not warnings.get("allWarningsAsErrors") or any(
        warning in warnings.get("noWarn", []) for warning in ("NU1901", "NU1902", "NU1903", "NU1904")
    ):
        failures.append(f"{project}: audit warnings must remain errors, not suppressed")

    for framework, specification in assets["project"]["frameworks"].items():
        direct = specification["dependencies"].get(package, {})
        if direct.get("version") != f"[{expected}, )" or direct.get("suppressParent") != "All":
            failures.append(f"{project}/{framework}: direct private patched dependency is missing: {direct}")

    for framework, target in assets["targets"].items():
        selected = [name for name in target if name.startswith(package + "/")]
        if selected != [f"{package}/{expected}"]:
            failures.append(f"{project}/{framework}: expected {package}/{expected}, got {selected}")
        else:
            library = target[selected[0]]
            if not library.get("compile") or not library.get("runtime"):
                failures.append(f"{project}/{framework}: dependency must remain compile/runtime-visible")
        print(f"{project}/{framework}: {selected}")

if failures:
    for failure in failures:
        print("FAIL: " + failure)
    raise SystemExit(1)

print("PASS: Uno.UI.Tasks retains patched private ASN.1 assets and NuGet auditing.")
