"""Exercise fail-closed fixtures for the Uno.UI.Tasks dependency graph validator."""
import base64
import copy
import json
from pathlib import Path
import subprocess
import sys
import tempfile

validator = Path(__file__).with_name("validate-uno-ui-tasks-crypto-graph.py")
project_framework = "netstandard2.0"
target_framework = ".NETStandard,Version=v2.0"
package = "System.Formats.Asn1"
version = "8.0.1"
library = f"{package}/{version}"
asset = "lib/netstandard2.0/System.Formats.Asn1.dll"


def patched_graph():
    return {
        "project": {
            "restore": {
                "restoreAuditProperties": {
                    "enableAudit": "true",
                    "auditMode": "all",
                },
                "warningProperties": {
                    "allWarningsAsErrors": True,
                    "noWarn": ["NU5123"],
                },
            },
            "frameworks": {
                project_framework: {
                    "dependencies": {
                        package: {
                            "version": f"[{version}, )",
                            "suppressParent": "All",
                        }
                    }
                }
            },
        },
        "targets": {
            target_framework: {
                library: {
                    "type": "package",
                    "compile": {asset: {}},
                    "runtime": {asset: {}},
                }
            }
        },
        "libraries": {
            library: {
                "type": "package",
                "path": f"{package.lower()}/{version}",
                "sha512": base64.b64encode(b"X" * 64).decode(),
                "files": [asset],
            }
        },
    }


positive = patched_graph()
empty = patched_graph()
empty["project"]["frameworks"] = {}
empty["targets"] = {}
empty["libraries"] = {}

partial = patched_graph()
partial["targets"][target_framework][library].pop("runtime")
partial["libraries"][library].pop("files")

affected = patched_graph()
affected_library = f"{package}/7.0.0"
affected["project"]["frameworks"][project_framework]["dependencies"][package]["version"] = "[7.0.0, )"
affected["targets"][target_framework][affected_library] = affected["targets"][target_framework].pop(library)
affected["libraries"][affected_library] = affected["libraries"].pop(library)
affected["libraries"][affected_library]["path"] = f"{package.lower()}/7.0.0"

missing_direct = patched_graph()
missing_direct["project"]["frameworks"][project_framework]["dependencies"].pop(package)

untyped_warning_flag = patched_graph()
untyped_warning_flag["project"]["restore"]["warningProperties"]["allWarningsAsErrors"] = "true"

cases = [
    ("empty", empty, 1, ("expected project framework", "expected resolved target", "package library metadata")),
    ("partial", partial, 1, ("patched compile/runtime metadata is incomplete", "package files metadata")),
    ("affected", affected, 1, ("direct private patched dependency is missing", f"expected {library}")),
    ("missing-direct", missing_direct, 1, ("direct private patched dependency is missing",)),
    ("untyped-warning-flag", untyped_warning_flag, 1, ("audit warnings must remain errors",)),
    ("patched-positive", positive, 0, ("PASS:",)),
]

with tempfile.TemporaryDirectory(prefix="uno-ui-tasks-crypto-graph-") as temporary:
    temporary_path = Path(temporary)
    for name, fixture, expected_exit, expected_output in cases:
        fixture_path = temporary_path / f"{name}.json"
        fixture_path.write_text(json.dumps(copy.deepcopy(fixture)), encoding="utf-8")
        result = subprocess.run(
            [sys.executable, str(validator), "--assets", str(fixture_path)],
            check=False,
            capture_output=True,
            text=True,
        )
        output = result.stdout + result.stderr
        if result.returncode != expected_exit:
            raise AssertionError(f"{name}: expected exit {expected_exit}, got {result.returncode}\n{output}")
        for text in expected_output:
            if text not in output:
                raise AssertionError(f"{name}: missing output {text!r}\n{output}")
        print(f"PASS: {name} exited {result.returncode} with the expected graph result.")

print(f"PASS: {len(cases)} Uno.UI.Tasks crypto graph fixtures.")
