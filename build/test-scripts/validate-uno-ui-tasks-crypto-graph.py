"""Check Uno.UI.Tasks restores the patched ASN.1 dependency with auditing."""
import argparse
import base64
import binascii
import json
from pathlib import Path

PROJECT = "Uno.UI.Tasks"
PACKAGE = "System.Formats.Asn1"
EXPECTED_VERSION = "8.0.1"
EXPECTED_PROJECT_FRAMEWORK = "netstandard2.0"
EXPECTED_TARGET_FRAMEWORK = ".NETStandard,Version=v2.0"
EXPECTED_LIBRARY = f"{PACKAGE}/{EXPECTED_VERSION}"
EXPECTED_ASSET = "lib/netstandard2.0/System.Formats.Asn1.dll"


def get_object(container, key, label, failures):
    value = container.get(key) if isinstance(container, dict) else None
    if not isinstance(value, dict):
        failures.append(f"{PROJECT}: expected {label} object")
        return {}
    return value


def validate(assets):
    failures = []
    project = get_object(assets, "project", "project", failures)
    restore = get_object(project, "restore", "project restore", failures)
    audit = get_object(restore, "restoreAuditProperties", "restore audit properties", failures)
    if audit.get("enableAudit") not in (True, "true") or audit.get("auditMode") != "all":
        failures.append(f"{PROJECT}: NuGetAudit must remain enabled in all mode: {audit}")

    warnings = get_object(restore, "warningProperties", "restore warning properties", failures)
    no_warn = warnings.get("noWarn", [])
    if not isinstance(no_warn, list):
        failures.append(f"{PROJECT}: restore noWarn metadata must be a JSON array: {no_warn}")
        no_warn = []
    if warnings.get("allWarningsAsErrors") is not True or any(
        warning in no_warn for warning in ("NU1901", "NU1902", "NU1903", "NU1904")
    ):
        failures.append(f"{PROJECT}: audit warnings must remain errors, not suppressed")

    frameworks = get_object(project, "frameworks", "project frameworks", failures)
    if EXPECTED_PROJECT_FRAMEWORK not in frameworks:
        failures.append(f"{PROJECT}: expected project framework {EXPECTED_PROJECT_FRAMEWORK} is missing")
    elif set(frameworks) != {EXPECTED_PROJECT_FRAMEWORK}:
        failures.append(f"{PROJECT}: unexpected project frameworks: {sorted(frameworks)}")
    else:
        specification = get_object(
            frameworks, EXPECTED_PROJECT_FRAMEWORK, f"{EXPECTED_PROJECT_FRAMEWORK} specification", failures
        )
        dependencies = get_object(specification, "dependencies", "project dependencies", failures)
        direct = get_object(dependencies, PACKAGE, f"direct {PACKAGE} dependency", failures)
        if direct.get("version") != f"[{EXPECTED_VERSION}, )" or direct.get("suppressParent") != "All":
            failures.append(
                f"{PROJECT}/{EXPECTED_PROJECT_FRAMEWORK}: direct private patched dependency is missing: {direct}"
            )

    targets = get_object(assets, "targets", "resolved targets", failures)
    if EXPECTED_TARGET_FRAMEWORK not in targets:
        failures.append(f"{PROJECT}: expected resolved target {EXPECTED_TARGET_FRAMEWORK} is missing")
    elif set(targets) != {EXPECTED_TARGET_FRAMEWORK}:
        failures.append(f"{PROJECT}: unexpected resolved targets: {sorted(targets)}")
    else:
        target = get_object(targets, EXPECTED_TARGET_FRAMEWORK, "resolved target", failures)
        if not target:
            failures.append(f"{PROJECT}/{EXPECTED_TARGET_FRAMEWORK}: resolved target must not be empty")
        selected = [name for name in target if name.startswith(PACKAGE + "/")]
        if selected != [EXPECTED_LIBRARY]:
            failures.append(
                f"{PROJECT}/{EXPECTED_TARGET_FRAMEWORK}: expected {EXPECTED_LIBRARY}, got {selected}"
            )
        else:
            library = get_object(target, EXPECTED_LIBRARY, "resolved patched library", failures)
            compile_assets = get_object(library, "compile", "patched compile assets", failures)
            runtime_assets = get_object(library, "runtime", "patched runtime assets", failures)
            if (
                library.get("type") != "package"
                or EXPECTED_ASSET not in compile_assets
                or EXPECTED_ASSET not in runtime_assets
            ):
                failures.append(
                    f"{PROJECT}/{EXPECTED_TARGET_FRAMEWORK}: patched compile/runtime metadata is incomplete"
                )
        print(f"{PROJECT}/{EXPECTED_TARGET_FRAMEWORK}: {selected}")

    libraries = get_object(assets, "libraries", "package libraries", failures)
    package_metadata = get_object(libraries, EXPECTED_LIBRARY, "expected package library metadata", failures)
    files = package_metadata.get("files")
    if not isinstance(files, list) or EXPECTED_ASSET not in files:
        failures.append(f"{PROJECT}: patched package files metadata is incomplete")
    if (
        package_metadata.get("type") != "package"
        or package_metadata.get("path") != f"{PACKAGE.lower()}/{EXPECTED_VERSION}"
    ):
        failures.append(f"{PROJECT}: patched package identity metadata is invalid: {package_metadata}")
    package_hash = package_metadata.get("sha512")
    try:
        decoded_hash = base64.b64decode(package_hash, validate=True) if isinstance(package_hash, str) else b""
    except (binascii.Error, ValueError):
        decoded_hash = b""
    if len(decoded_hash) != 64:
        failures.append(f"{PROJECT}: patched package SHA512 metadata is missing or invalid")

    return failures


parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--source-root", type=Path, default=Path(__file__).resolve().parents[2])
parser.add_argument("--assets", type=Path)
args = parser.parse_args()
assets_path = args.assets or (
    args.source_root / "src" / "SourceGenerators" / PROJECT / "obj" / "project.assets.json"
)
failures = []

if not assets_path.is_file():
    failures.append(f"{PROJECT}: missing restored assets")
else:
    assets = json.loads(assets_path.read_text(encoding="utf-8-sig"))
    failures.extend(validate(assets))

if failures:
    for failure in failures:
        print("FAIL: " + failure)
    raise SystemExit(1)

print("PASS: Uno.UI.Tasks retains patched private ASN.1 assets and NuGet auditing.")
