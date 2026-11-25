#!/usr/bin/env bash
set -euo pipefail

repo=$(git -C "$(dirname -- "$0")" rev-parse --show-toplevel)
cd "$repo"
revision=$(git rev-parse HEAD)
if [[ $# -gt 0 && $1 != --* ]]; then
    artifacts=$1
    shift
else
    parent="artifacts/benchmarks/${revision:0:12}"
    mkdir -p -- "$parent"
    artifacts=$(mktemp -d "$parent/$(date -u +%Y%m%dT%H%M%SZ).XXXXXXXX")
fi
if [[ $# -eq 0 ]]; then
    set -- --filter '*'
fi

mkdir -p -- "$artifacts"
artifacts=$(realpath -- "$artifacts")
printf 'Benchmark artifacts %s\n' "$artifacts"
project=benchmarks/Lokad.GZip.Benchmarks
assembly="$project/bin/Release/net11.0/Lokad.GZip.Benchmarks.dll"
dotnet_path=$(command -v dotnet)

{
    printf 'Revision %s\n' "$revision"
    git status --porcelain --untracked-files=all
    printf 'UTC %s\n' "$(date -u +%FT%TZ)"
    uname -a
    lscpu
    dotnet --info
    printf 'Benchmark arguments:'
    printf ' %q' "$@"
    printf '\n'
} > "$artifacts/environment.txt"

dotnet restore Lokad.GZip.slnx --locked-mode
dotnet build "$project" -c Release --no-restore
dotnet exec --fx-version 11.0.0 "$assembly" --smoke | tee "$artifacts/smoke.txt"
uptime > "$artifacts/load-before.txt"
dotnet exec --fx-version 11.0.0 "$assembly" --cli "$dotnet_path" \
    --artifacts "$artifacts" --exporters json "$@" | tee "$artifacts/run.txt"
uptime > "$artifacts/load-after.txt"
