#!/usr/bin/env python3
"""Export clean committed source and verified history, including workspace repositories."""
from pathlib import Path
import subprocess
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def git(repository, *arguments):
    return subprocess.check_output(["git", *arguments], cwd=repository)


def main():
    repositories = [ROOT]
    if (ROOT / "repos").is_dir():
        repositories += sorted(path for path in (ROOT / "repos").iterdir() if (path / ".git").is_dir())
    for repository in repositories:
        if git(repository, "status", "--porcelain", "--untracked-files=all").strip():
            raise RuntimeError(f"Resolve uncommitted source (including README files) before exporting {repository.name}.")
    output = ROOT / "artifacts"
    output.mkdir(exist_ok=True)
    archive = output / f"{ROOT.name}-source.zip"
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as target:
        for repository in repositories:
            prefix = Path(ROOT.name) / repository.relative_to(ROOT)
            bundle = output / f"{repository.name}-history.bundle"
            git(repository, "bundle", "create", str(bundle), "--all")
            git(repository, "bundle", "verify", str(bundle))
            tracked = filter(None, git(repository, "ls-files", "-z").decode().split("\0"))
            for relative in tracked:
                source = repository / relative
                if not source.is_file():
                    raise RuntimeError(f"Tracked source is missing or is a submodule: {source}")
                target.write(source, prefix / relative)
            target.write(bundle, prefix / "history.bundle")
            target.writestr((prefix / "RESTORE-GIT.txt").as_posix(),
                           "Restore history into a separate directory with: git clone history.bundle restored\n")
    with zipfile.ZipFile(archive) as result:
        assert result.testzip() is None, "Corrupt source archive"
        assert sum(name.endswith("/history.bundle") for name in result.namelist()) == len(repositories)
    print(f"Created {archive} with source and verified history for {len(repositories)} repositories.")


if __name__ == "__main__":
    main()
