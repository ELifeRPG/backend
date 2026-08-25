# Keycloak for local dev: a stock server plus one build stage and one COPY per provider jar.
#
# A provider jar registers its own themes through META-INF/keycloak-themes.json, so the jar is
# all that needs copying — there is deliberately no theme directory to carry over.
#
# Keycloak is pinned alongside the providers because the SPI carries no cross-version
# compatibility guarantee.
#
# These ARGs are the single source of truth for the versions; they are deliberately not mirrored
# into compose.yml's build.args, which would mean maintaining the numbers in two places.

ARG KEYCLOAK_VERSION=26.0
ARG THEME_VERSION=1.0.1
ARG BOHEMIA_GAMEACCOUNT_VERSION=1.0.0

FROM ghcr.io/eliferpg/keycloak-theme-eliferpg:${THEME_VERSION} AS theme
FROM ghcr.io/eliferpg/keycloak-bohemia-gameaccount:${BOHEMIA_GAMEACCOUNT_VERSION} AS bohemia

FROM quay.io/keycloak/keycloak:${KEYCLOAK_VERSION}

COPY --from=theme /opt/keycloak/providers/ /opt/keycloak/providers/
COPY --from=bohemia /opt/keycloak/providers/ /opt/keycloak/providers/
