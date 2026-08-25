#!/usr/bin/env bash
# Adds Discord as an identity provider to the eliferpg realm.
#
# Applies to a RUNNING Keycloak over the Admin API rather than editing
# eliferpg-realm.json, and that is deliberate. Verified against Keycloak 26.0.8: a realm
# whose identityProviders reference a providerId the server does not have makes Keycloak
# refuse to start at all --
#
#   ERROR: Invalid identity provider id [discord]
#   ERROR: Failed to start server in (development) mode
#
# -- so committing a Discord block before the provider is actually in the image would
# brick `docker compose up` for everyone. Over the Admin API the same mistake is just a
# 4xx from this script. Use --print-realm-json once the provider really ships in the
# image and you want the realm export to own it.
#
# Secrets never land in the repo: real values come from the environment here, and
# --print-realm-json emits ${DISCORD_CLIENT_ID}/${DISCORD_CLIENT_SECRET} placeholders,
# which Keycloak substitutes from the environment at import time (verified on 26.0.8).
set -euo pipefail

REALM="eliferpg"
SERVER="http://localhost:8180"
ADMIN_USER="admin"
ADMIN_PASSWORD="admin"
ALIAS="discord"
PROVIDER_ID="discord"
PRINT_REALM_JSON=false
ALLOW_UNSUPPORTED=false

# Discord's OAuth2 endpoints. Discord is OAuth2, not OpenID Connect -- see the
# preflight below for why that matters.
AUTHORIZATION_URL="https://discord.com/oauth2/authorize"
TOKEN_URL="https://discord.com/api/oauth2/token"
USERINFO_URL="https://discord.com/api/users/@me"
DEFAULT_SCOPE="identify email"

usage() {
  cat <<'USAGE'
Usage:
  DISCORD_CLIENT_ID=... DISCORD_CLIENT_SECRET=... ./add-discord-idp.sh [options]

Options:
  --realm NAME              Realm to add the provider to      (default: eliferpg)
  --server URL              Keycloak base URL                 (default: http://localhost:8180)
  --admin-user NAME         Master-realm admin                (default: admin)
  --admin-password PASS     Master-realm admin password       (default: admin)
  --alias NAME              Identity provider alias           (default: discord)
  --provider-id ID          Keycloak provider factory id      (default: discord)
  --print-realm-json        Print the realm JSON block (placeholders, no secrets) and exit
  --allow-unsupported-provider
                            Create the provider even if the server does not offer
                            --provider-id. Only meaningful with --provider-id oidc, and
                            the resulting login will fail -- see the preflight output.
  -h, --help                Show this help

Environment:
  DISCORD_CLIENT_ID, DISCORD_CLIENT_SECRET   from https://discord.com/developers/applications
                                             (required unless --print-realm-json)

Discord application setup: add a redirect URI of
  <server>/realms/<realm>/broker/<alias>/endpoint
e.g. http://localhost:8180/realms/eliferpg/broker/discord/endpoint
USAGE
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --realm) REALM="$2"; shift 2 ;;
    --server) SERVER="${2%/}"; shift 2 ;;
    --admin-user) ADMIN_USER="$2"; shift 2 ;;
    --admin-password) ADMIN_PASSWORD="$2"; shift 2 ;;
    --alias) ALIAS="$2"; shift 2 ;;
    --provider-id) PROVIDER_ID="$2"; shift 2 ;;
    --print-realm-json) PRINT_REALM_JSON=true; shift ;;
    --allow-unsupported-provider) ALLOW_UNSUPPORTED=true; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

# --- the realm-JSON block, for when the provider genuinely ships in the image --------
if [[ "$PRINT_REALM_JSON" == true ]]; then
  cat <<JSON
{
  "alias": "${ALIAS}",
  "providerId": "${PROVIDER_ID}",
  "enabled": true,
  "trustEmail": true,
  "storeToken": false,
  "linkOnly": false,
  "firstBrokerLoginFlowAlias": "first broker login",
  "config": {
    "clientId": "\${DISCORD_CLIENT_ID}",
    "clientSecret": "\${DISCORD_CLIENT_SECRET}",
    "authorizationUrl": "${AUTHORIZATION_URL}",
    "tokenUrl": "${TOKEN_URL}",
    "userInfoUrl": "${USERINFO_URL}",
    "defaultScope": "${DEFAULT_SCOPE}",
    "syncMode": "IMPORT"
  }
}
JSON
  cat >&2 <<'WARN'

Note: paste this into eliferpg-realm.json's "identityProviders" array ONLY once the
provider id above actually exists in the Keycloak image. If it does not, Keycloak
refuses to start -- "Invalid identity provider id" -- and the whole stack fails to come
up, not just Discord login.

The ${...} placeholders are substituted from the environment at import time, so the
secret stays out of git. Set DISCORD_CLIENT_ID and DISCORD_CLIENT_SECRET on the Keycloak
container (compose.yml `environment:`).
WARN
  exit 0
fi

: "${DISCORD_CLIENT_ID:?set DISCORD_CLIENT_ID (see --help)}"
: "${DISCORD_CLIENT_SECRET:?set DISCORD_CLIENT_SECRET (see --help)}"

echo "==> Authenticating against ${SERVER} as ${ADMIN_USER}"
ADMIN_TOKEN=$(curl -sf -X POST "${SERVER}/realms/master/protocol/openid-connect/token" \
  -d "grant_type=password&client_id=admin-cli&username=${ADMIN_USER}&password=${ADMIN_PASSWORD}" \
  | python3 -c "import sys,json;print(json.load(sys.stdin)['access_token'])") || {
    echo "ERROR: could not get an admin token from ${SERVER}." >&2; exit 1; }

auth=(-H "Authorization: Bearer ${ADMIN_TOKEN}")
json=(-H "Content-Type: application/json")

# --- preflight: is this provider actually installed? --------------------------------
probe=$(curl -s -o /dev/null -w '%{http_code}' "${auth[@]}" \
  "${SERVER}/admin/realms/${REALM}/identity-provider/providers/${PROVIDER_ID}")

if [[ "$probe" != "200" ]]; then
  cat >&2 <<EOF

ERROR: this Keycloak has no identity provider with id "${PROVIDER_ID}"
       (${SERVER} answered ${probe}).

Stock Keycloak 26.0.8 ships these: bitbucket, facebook, github, gitlab, google,
instagram, keycloak-oidc, linkedin-openid-connect, microsoft, oidc, openshift-v3,
openshift-v4, paypal, saml, stackoverflow, twitter. There is no Discord provider.

Configuring Discord as the generic "oidc" provider does NOT work. Verified end to end
against 26.0.8 with a stand-in Discord that mimics its real responses -- three separate
incompatibilities, each of which stops the login:

  1. Discord's token response has no id_token (it is OAuth2, not OpenID Connect)
       -> IdentityBrokerException: No token from server.
  2. Discord's /users/@me returns "id", not "sub"
       -> IdentityBrokerException: Could not fetch attributes from userinfo endpoint.
  3. Keycloak validates the OIDC nonce round-trip
       -> IdentityBrokerException: OpenID Provider [oidc] did not return a nonce

Discord therefore needs a dedicated identity provider extension (an
AbstractOAuth2IdentityProvider subclass) added to the Keycloak image, the same way this
project already ships keycloak-bohemia-gameaccount. Once that jar is in the image and reports a
provider id, re-run this script with --provider-id <that id>.

Re-run with --allow-unsupported-provider to create it anyway (login will fail).
EOF
  [[ "$ALLOW_UNSUPPORTED" == true ]] || exit 1
  echo "WARNING: --allow-unsupported-provider given; continuing anyway." >&2
fi

# --- create or update, idempotently -------------------------------------------------
payload=$(DISCORD_CLIENT_ID="$DISCORD_CLIENT_ID" DISCORD_CLIENT_SECRET="$DISCORD_CLIENT_SECRET" \
  ALIAS="$ALIAS" PROVIDER_ID="$PROVIDER_ID" AUTHORIZATION_URL="$AUTHORIZATION_URL" \
  TOKEN_URL="$TOKEN_URL" USERINFO_URL="$USERINFO_URL" DEFAULT_SCOPE="$DEFAULT_SCOPE" \
  python3 -c '
import json, os
print(json.dumps({
    "alias": os.environ["ALIAS"],
    "providerId": os.environ["PROVIDER_ID"],
    "enabled": True,
    # Discord verifies email addresses itself and reports it, so a brokered login does
    # not need Keycloak to re-verify. This is also what lets first-broker-login offer
    # "add to existing account" instead of forcing a second, duplicate user.
    "trustEmail": True,
    "storeToken": False,
    "linkOnly": False,
    "firstBrokerLoginFlowAlias": "first broker login",
    "config": {
        "clientId": os.environ["DISCORD_CLIENT_ID"],
        "clientSecret": os.environ["DISCORD_CLIENT_SECRET"],
        "authorizationUrl": os.environ["AUTHORIZATION_URL"],
        "tokenUrl": os.environ["TOKEN_URL"],
        "userInfoUrl": os.environ["USERINFO_URL"],
        "defaultScope": os.environ["DEFAULT_SCOPE"],
        "syncMode": "IMPORT",
    },
}))')

exists=$(curl -s -o /dev/null -w '%{http_code}' "${auth[@]}" \
  "${SERVER}/admin/realms/${REALM}/identity-provider/instances/${ALIAS}")

if [[ "$exists" == "200" ]]; then
  echo "==> Updating existing identity provider '${ALIAS}'"
  status=$(curl -s -o /tmp/idp-resp.$$ -w '%{http_code}' -X PUT "${auth[@]}" "${json[@]}" \
    "${SERVER}/admin/realms/${REALM}/identity-provider/instances/${ALIAS}" -d "$payload")
else
  echo "==> Creating identity provider '${ALIAS}'"
  status=$(curl -s -o /tmp/idp-resp.$$ -w '%{http_code}' -X POST "${auth[@]}" "${json[@]}" \
    "${SERVER}/admin/realms/${REALM}/identity-provider/instances" -d "$payload")
fi

if [[ "$status" != "201" && "$status" != "204" ]]; then
  echo "ERROR: Keycloak answered ${status}:" >&2
  cat /tmp/idp-resp.$$ >&2; echo >&2
  rm -f /tmp/idp-resp.$$
  exit 1
fi
rm -f /tmp/idp-resp.$$

# --- attribute mappers ---------------------------------------------------------------
# Discord's userinfo field names, not OIDC's. Mapped explicitly so a brokered signup
# lands with a real username and email rather than a generated one.
add_mapper() {
  local name="$1" claim="$2" user_attribute="$3"
  local body
  body=$(NAME="$name" CLAIM="$claim" ATTR="$user_attribute" ALIAS="$ALIAS" python3 -c '
import json, os
print(json.dumps({
    "name": os.environ["NAME"],
    "identityProviderAlias": os.environ["ALIAS"],
    "identityProviderMapper": "oidc-user-attribute-idp-mapper",
    "config": {
        "claim": os.environ["CLAIM"],
        "user.attribute": os.environ["ATTR"],
        "syncMode": "INHERIT",
    },
}))')
  local code
  code=$(curl -s -o /dev/null -w '%{http_code}' -X POST "${auth[@]}" "${json[@]}" \
    "${SERVER}/admin/realms/${REALM}/identity-provider/instances/${ALIAS}/mappers" -d "$body")
  # 409 means it is already there, which is the idempotent case, not a failure.
  case "$code" in
    201|409) printf '    %-18s %s -> %s (%s)\n' "$name" "$claim" "$user_attribute" "$code" ;;
    *)       printf '    %-18s FAILED (%s)\n' "$name" "$code" >&2 ;;
  esac
}

echo "==> Attribute mappers"
add_mapper "discord-username" "username"    "username"
add_mapper "discord-email"    "email"       "email"
add_mapper "discord-nickname" "global_name" "firstName"

cat <<EOF

Done. Discord is configured on realm '${REALM}' as alias '${ALIAS}'.

Next:
  1. In the Discord application, add this redirect URI:
       ${SERVER}/realms/${REALM}/broker/${ALIAS}/endpoint
  2. Sign in at the portal and pick Discord.

A player who already has a username/password account and whose Discord email matches it
is offered "add to existing account" by the first-broker-login flow, keeps their existing
account, and gains the federated identity. Matching is on email only, so a Discord
account with a different email creates a second, separate user.
EOF
