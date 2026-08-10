"""Erlaubt ``python -m jarvis`` ohne Installation."""

import sys

from .cli import main

if __name__ == "__main__":
    sys.exit(main())
