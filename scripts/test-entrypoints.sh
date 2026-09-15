#!/bin/bash
set -euo pipefail

PROJECT_DIRECTORY="$(cd "$(dirname "$0")/.." && pwd -P)"

test ! -e "${PROJECT_DIRECTORY}/Resources/Codex-OpenAI.command"
test ! -e "${PROJECT_DIRECTORY}/Resources/Codex-Sub2API.command"
! grep -Fq 'Desktop/${script_name}' \
    "${PROJECT_DIRECTORY}/scripts/build-and-install.sh"
grep -Fq '.app.backup' \
    "${PROJECT_DIRECTORY}/scripts/build-and-install.sh"
grep -Fq '"${LAUNCH_SERVICES_REGISTER}" -u' \
    "${PROJECT_DIRECTORY}/scripts/build-and-install.sh"
