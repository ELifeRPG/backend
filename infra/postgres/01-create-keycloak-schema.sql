-- Keycloak is configured with KC_DB_SCHEMA=keycloak (see compose.yml), but Keycloak does not
-- create that schema itself — it fails to start with `schema "keycloak" does not exist` against a
-- freshly initialised Postgres volume. Nothing else created it, so `docker compose down -v` used to
-- leave the stack unable to come back up.
--
-- Scripts in /docker-entrypoint-initdb.d are run by the postgres image only on first initialisation
-- of the data directory, which is exactly when the schema is missing.
CREATE SCHEMA IF NOT EXISTS keycloak;
