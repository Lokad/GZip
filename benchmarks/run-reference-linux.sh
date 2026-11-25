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
runtime_version=11.0.0

{
    printf 'Revision %s\n' "$revision"
    git status --porcelain --untracked-files=all
    printf 'UTC %s\n' "$(date -u +%FT%TZ)"
    uname -a
    lscpu
    dotnet --info
    printf 'Benchmark runtime .NET %s\n' "$runtime_version"
    printf 'Benchmark arguments:'
    printf ' %q' "$@"
    printf '\n'
} > "$artifacts/environment.txt"

dotnet restore Lokad.GZip.slnx --locked-mode
dotnet build "$project" -c Release --no-restore
DOTNET_ROLL_FORWARD=Disable dotnet exec --fx-version "$runtime_version" "$assembly" --smoke | tee "$artifacts/smoke.txt"
uptime > "$artifacts/load-before.txt"
DOTNET_ROLL_FORWARD=Disable dotnet exec --fx-version "$runtime_version" "$assembly" --cli "$dotnet_path" \
    --artifacts "$artifacts" --exporters json "$@" | tee "$artifacts/run.txt"
uptime > "$artifacts/load-after.txt"
