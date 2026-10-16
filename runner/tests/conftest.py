"""Make runner/ importable for the tests (run_days.py does the same by living in that folder)."""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
