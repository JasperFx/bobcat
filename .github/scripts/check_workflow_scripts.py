#!/usr/bin/env python3
"""Syntax-check the inline `run:` scripts in every workflow.

publish.yml runs only on a `v*` tag push, so nothing parses it until a release is already
underway. A stray `fi` shipped that way once and failed the 0.16.0 run *after* all twelve
packages were live -- the release was fine and the workflow was red. This gate is the cheapest
thing that would have caught it: `bash -n` over each script, on every push.

It checks syntax only. `bash -n` does not run a command or resolve a variable, so a passing
script can still be wrong -- it just cannot be unparseable.
"""

import re
import subprocess
import sys
import tempfile
from pathlib import Path

import yaml

# A `${{ … }}` expression is substituted by Actions before bash ever sees the script, but bash
# reads `${{` as a malformed parameter expansion. Swap in a plain word so the check is about the
# author's shell, not about template syntax.
EXPRESSION = re.compile(r"\$\{\{.*?\}\}", re.DOTALL)

# Anything whose shell is not bash belongs to a different parser.
BASH_SHELLS = {None, "bash", "sh", "bash -e {0}", "/usr/bin/bash", "/bin/bash"}


def steps_of(document):
    for job_name, job in (document.get("jobs") or {}).items():
        if not isinstance(job, dict):
            continue
        for index, step in enumerate(job.get("steps") or []):
            if isinstance(step, dict) and "run" in step:
                yield job_name, index, step


def main() -> int:
    root = Path(__file__).resolve().parents[2]
    workflows = sorted((root / ".github" / "workflows").glob("*.y*ml"))
    if not workflows:
        print("::error::No workflows found to check")
        return 1

    failures = 0
    checked = 0

    for path in workflows:
        # A workflow that is not valid YAML never runs at all, which is a worse version of the
        # problem this gate exists for -- report it as an error rather than a stack trace.
        try:
            document = yaml.safe_load(path.read_text())
        except yaml.YAMLError as e:
            failures += 1
            print(f"::error file=.github/workflows/{path.name}::not valid YAML — {e}")
            continue

        if not isinstance(document, dict):
            continue

        for job_name, index, step in steps_of(document):
            shell = step.get("shell") or (document.get("defaults", {}).get("run", {}) or {}).get("shell")
            if shell not in BASH_SHELLS:
                continue

            name = step.get("name") or f"step {index + 1}"
            script = EXPRESSION.sub("GHA_EXPRESSION", step["run"])
            checked += 1

            with tempfile.NamedTemporaryFile("w", suffix=".sh", delete=False) as handle:
                handle.write(script)
                temp = handle.name

            result = subprocess.run(["bash", "-n", temp], capture_output=True, text=True)
            if result.returncode != 0:
                failures += 1
                detail = result.stderr.replace(temp, f"{path.name}:{job_name}:{name}").strip()
                print(f"::error file={path.relative_to(root)}::{job_name} / {name} — {detail}")

    print(f"Checked {checked} inline script(s) across {len(workflows)} workflow(s).")

    if failures:
        print(f"::error::{failures} workflow script(s) will not parse")
        return 1

    return 0


if __name__ == "__main__":
    sys.exit(main())
