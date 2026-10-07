#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet format Motiva.sln --verify-no-changes
