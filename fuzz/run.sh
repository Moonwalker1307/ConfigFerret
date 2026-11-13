#!/usr/bin/env bash
# Builds the fuzz harness, instruments it with SharpFuzz, and runs one target under libFuzzer.
#
#   fuzz/run.sh <target> [seconds] [extra libFuzzer flags...]
#
# Needs Linux, the .NET 10 SDK and clang with the libFuzzer runtime (libclang-rt-dev on Ubuntu).
# Crashes land in fuzz/findings/<target>/, and the corpus grows in fuzz/work/<target>/corpus/.
# Replay a crash without libFuzzer: fuzz/out/ConfigFerret.Fuzz <target> <crash-file>
set -euo pipefail

target="${1:?usage: fuzz/run.sh <target> [seconds] [libFuzzer flags...]}"
seconds="${2:-60}"
shift $(( $# >= 2 ? 2 : 1 ))

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
out="$here/out"
tools="$here/.tools"
work="$here/work/$target"
findings="$here/findings/$target"

# Pinned libfuzzer-dotnet driver (MIT, https://github.com/Metalnem/libfuzzer-dotnet).
driver_commit="bd39d4e88d715ab460a929943645be2a186cde52"
driver_sha256="90f019e2e9ad3a0b93c7ecc2c5afb2fbfc8b5aab6aac51c7e0d349ec79354f36"

mkdir -p "$tools" "$work/corpus" "$findings"

if [[ ! -x "$tools/libfuzzer-dotnet" ]]; then
  if [[ ! -f "$tools/libfuzzer-dotnet.cc" ]]; then
    curl -fsSL -o "$tools/libfuzzer-dotnet.cc" \
      "https://raw.githubusercontent.com/Metalnem/libfuzzer-dotnet/$driver_commit/libfuzzer-dotnet.cc"
  fi
  echo "$driver_sha256  $tools/libfuzzer-dotnet.cc" | sha256sum -c -
  "${CXX:-clang++}" -O2 -fsanitize=fuzzer "$tools/libfuzzer-dotnet.cc" -o "$tools/libfuzzer-dotnet"
fi

# Build once per checkout; delete fuzz/out to rebuild.
if [[ ! -f "$out/.instrumented" ]]; then
  rm -rf "$out"
  dotnet publish "$here/ConfigFerret.Fuzz" -c Release -f net10.0 -o "$out" -p:TargetFrameworks=net10.0 --nologo -v quiet
  # The harness instruments with the SharpFuzz library it references, so no separate tool is needed.
  "$out/ConfigFerret.Fuzz" --instrument "$out/ConfigFerret.dll" "$out/ConfigFerret.GitHub.dll" "$out/ConfigFerret.Octopus.dll" "$out/YamlDotNet.dll"
  touch "$out/.instrumented"
fi

dict=()
case "$target" in
  json|unused) dict=(-dict="$here/dictionaries/json.dict") ;;
  github|octopus) dict=(-dict="$here/dictionaries/api.dict") ;;
  env-names) ;;
  *) dict=(-dict="$here/dictionaries/yaml.dict") ;;
esac

# Seeds first, then whatever earlier runs found. New inputs are written to the first directory.
"$tools/libfuzzer-dotnet" \
  --target_path="$out/ConfigFerret.Fuzz" \
  --target_arg="$target" \
  -max_total_time="$seconds" \
  -timeout=10 \
  -rss_limit_mb=4096 \
  -max_len=65536 \
  -print_final_stats=1 \
  -artifact_prefix="$findings/" \
  "${dict[@]}" \
  "$@" \
  "$work/corpus" "$here/corpus/$target"
