#!/usr/bin/env bash

set -euo pipefail

target="${1:-BadmintonDraw.sln}"
audit_output="$(dotnet list "$target" package --vulnerable --include-transitive --format json --output-version 1)"

printf '%s\n' "$audit_output"

if grep -qi '"advisoryurl"' <<<"$audit_output"; then
  printf '%s\n' "Vulnerable NuGet packages were found." >&2
  exit 1
fi

printf '%s\n' "No vulnerable NuGet packages found."
