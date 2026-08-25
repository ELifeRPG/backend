FROM ghcr.io/eliferpg/keycloak-theme-eliferpg:1.0.1 AS theme
FROM ghcr.io/eliferpg/keycloak-bohemia-gameaccount:1.0.0 AS bohemia

FROM quay.io/keycloak/keycloak:26.0
COPY --from=theme /opt/keycloak/providers/ /opt/keycloak/providers/
COPY --from=bohemia /opt/keycloak/providers/ /opt/keycloak/providers/
