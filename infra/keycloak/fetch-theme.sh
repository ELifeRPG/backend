#!/usr/bin/env bash
set -euo pipefail

# Downloads the latest eliferpg Keycloak theme jar (built by
# ELifeRPG/keycloak-theme-eliferpg's release workflow) into
# infra/keycloak/providers/, where compose.yml mounts it into Keycloak's
# providers/ directory. Uses the "kc-all-other-versions" jar variant, which
# is the one that covers this stack's Keycloak 26.0 (see that repo's
# README for the version matrix).
#
# Requires the gh CLI, authenticated against GitHub.

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/providers"
mkdir -p "$DIR"

gh release download --repo ELifeRPG/keycloak-theme-eliferpg \
  --pattern 'keycloak-theme-for-kc-all-other-versions.jar' \
  --dir "$DIR" \
  --clobber

echo "Theme jar downloaded to $DIR. Run 'docker compose up -d keycloak' (or restart it) to pick it up."
