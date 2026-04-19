#!/command/with-contenv sh
# s6 service: Cove application
# Waits for PostgreSQL to be ready, creates the database, then starts Cove.

PGDATA="${PGDATA:-/var/lib/postgresql/cove-data}"
DB_NAME="${COVE__Postgres__Database:-cove}"
DB_USER="cove"

# Wait for PostgreSQL to accept connections
echo "[cove] Waiting for PostgreSQL..."
for i in $(seq 1 30); do
    if su - postgres -c "/usr/lib/postgresql/17/bin/pg_isready -q -p 5432" 2>/dev/null; then
        break
    fi
    sleep 1
done

# Create user and database if they don't exist
su - postgres -c "psql -p 5432 -tc \"SELECT 1 FROM pg_roles WHERE rolname='$DB_USER'\" | grep -q 1 || psql -p 5432 -c \"CREATE ROLE $DB_USER WITH LOGIN PASSWORD 'cove';\""
su - postgres -c "psql -p 5432 -tc \"SELECT 1 FROM pg_database WHERE datname='$DB_NAME'\" | grep -q 1 || psql -p 5432 -c \"CREATE DATABASE $DB_NAME OWNER $DB_USER;\""

# Enable pgvector extension
su - postgres -c "psql -p 5432 -d $DB_NAME -c 'CREATE EXTENSION IF NOT EXISTS vector;'" 2>/dev/null || true

echo "[cove] Starting Cove..."
cd /opt/cove
exec dotnet Cove.Api.dll
