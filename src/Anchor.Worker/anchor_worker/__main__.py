from __future__ import annotations

import argparse
import asyncio

from .server import serve


def main() -> None:
    parser = argparse.ArgumentParser(description="Anchor local inference worker")
    parser.add_argument("--port", type=int, default=0)
    parser.add_argument("--token", required=True)
    arguments = parser.parse_args()
    asyncio.run(serve(arguments.port, arguments.token))


if __name__ == "__main__":
    main()
