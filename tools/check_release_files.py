"""Check package metadata and the copied website documentation before release."""

import json
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
DOCS = ROOT / "docs"
SITE_DOCS = ROOT / "website" / "docs"
PACKAGE = ROOT / "client" / "Unity"


def main() -> None:
    package = json.loads((PACKAGE / "package.json").read_text(encoding="utf-8"))
    assert package["license"] == "MPL-2.0"
    assert (ROOT / "LICENSE").exists()
    assert (PACKAGE / "LICENSE.md").exists()
    assert (PACKAGE / "NOTICE.md").exists()
    assert (PACKAGE / package["samples"][0]["path"]).is_dir()

    source_docs = {p.relative_to(DOCS) for p in DOCS.rglob("*.md") if p.name != "README.md"}
    site_docs = {p.relative_to(SITE_DOCS) for p in SITE_DOCS.rglob("*.md")}
    assert source_docs == site_docs, f"Documentation file mismatch: {source_docs ^ site_docs}"
    for relative in source_docs:
        assert (DOCS / relative).read_bytes() == (SITE_DOCS / relative).read_bytes(), (
            f"Website copy differs from docs/{relative}"
        )

    source_namespace = "SomniumSpace.Worlds.bently.networking"
    for script in (PACKAGE / "Runtime").rglob("*.cs"):
        assert source_namespace in script.read_text(encoding="utf-8"), script
    print(f"Package metadata and {len(source_docs)} website docs checked")


if __name__ == "__main__":
    main()
