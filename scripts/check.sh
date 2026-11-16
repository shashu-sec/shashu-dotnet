#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
step() { echo "== $* =="; }

step "dotnet format"
dotnet format Shashu.Sdk.sln
dotnet format Shashu.Sdk.sln --verify-no-changes

step "build + test + pack"
dotnet build Shashu.Sdk/Shashu.Sdk.csproj -c Release
dotnet test Shashu.Sdk.sln -c Release --no-restore
dotnet pack Shashu.Sdk/Shashu.Sdk.csproj -c Release --no-build -o "$ROOT/nupkg"

step "nupkg contents"
python - <<'PY'
import zipfile, sys
from pathlib import Path
z = zipfile.ZipFile(Path("nupkg")/"Shashu.Sdk.1.0.0.nupkg")
names = z.namelist()
assert any(n.endswith("README.md") for n in names), names
assert any("LICENSE" in n for n in names), names
bad = [n for n in names if "_internal" in n or "IDENTITY-I4" in n or "Shashu.Sdk.Tests" in n]
if bad:
    print(bad); sys.exit(1)
PY

step "vulnerable packages"
dotnet list Shashu.Sdk/Shashu.Sdk.csproj package --vulnerable --include-transitive

step "gitleaks"
command -v gitleaks >/dev/null || { echo "gitleaks missing"; exit 1; }
gitleaks detect --no-git --source "$ROOT" --redact

echo "ALL CHECKS PASSED"
