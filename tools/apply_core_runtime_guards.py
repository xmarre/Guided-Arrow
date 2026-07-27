#!/usr/bin/env python3
"""Apply idempotent runtime guards to the buildable Guided Arrow core source."""

from __future__ import annotations

import pathlib
import sys


def replace_once(text: str, old: str, new: str, label: str) -> str:
    if new in text:
        return text
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"Expected exactly one {label} marker, found {count}")
    return text.replace(old, new, 1)


def main() -> int:
    if len(sys.argv) != 2:
        raise SystemExit("usage: apply_core_runtime_guards.py <core-source-directory>")
    root = pathlib.Path(sys.argv[1]).resolve()

    bridge_path = root / "MissileDamageBridge.cs"
    bridge = bridge_path.read_text(encoding="utf-8-sig")
    bridge = replace_once(
        bridge,
        "\tinternal static string InstallFailure => _installFailure ?? string.Empty;",
        "\tinternal static string InstallFailure => _installFailure ?? string.Empty;\n\n\tinternal static bool IsSyntheticOverrideActive => _activeOverride != null;",
        "synthetic-override property",
    )
    bridge_path.write_text(bridge, encoding="utf-8")

    behavior_path = root / "GuidedArrowBehavior.cs"
    behavior = behavior_path.read_text(encoding="utf-8-sig")
    behavior = replace_once(
        behavior,
        """\t\tif (_state != State.Idle)\n\t\t{\n\t\t\tif (isAlliedTakeover)\n\t\t\t{\n\t\t\t\tQueueAlliedTakeover(shooterAgent, forcedMissileIndex, position, velocity);\n\t\t\t}""",
        """\t\tif (_state != State.Idle)\n\t\t{\n\t\t\tif (MissileDamageBridge.IsSyntheticOverrideActive)\n\t\t\t{\n\t\t\t\tLog(\"Ignored Guided Arrow's own synthetic missile callback while the active shot remained in flight.\");\n\t\t\t}\n\t\t\telse if (isAlliedTakeover)\n\t\t\t{\n\t\t\t\tQueueAlliedTakeover(shooterAgent, forcedMissileIndex, position, velocity);\n\t\t\t}""",
        "synthetic callback guard",
    )
    behavior_path.write_text(behavior, encoding="utf-8")

    print("Applied synthetic-callback isolation guard")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
