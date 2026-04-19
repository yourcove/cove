#!/command/with-contenv sh
# s6 service: PostgreSQL
# Initializes data directory if needed, then runs PostgreSQL.

PGDATA="${PGDATA:-/var/lib/postgresql/cove-data}"

# Initialize if fresh
if [ ! -f "$PGDATA/PG_VERSION" ]; then
    echo "[postgres] Initializing data directory..."
    chown -R postgres:postgres "$PGDATA"
    su - postgres -c "/usr/lib/postgresql/17/bin/initdb -D '$PGDATA' --auth=trust --encoding=UTF8 --locale=C"

    # Allow local connections without password (container-internal only)
    echo "host all all 127.0.0.1/32 trust" >> "$PGDATA/pg_hba.conf"
    echo "host all all ::1/128 trust" >> "$PGDATA/pg_hba.conf"
fi

# Ensure correct ownership
chown -R postgres:postgres "$PGDATA"

# Start PostgreSQL in foreground (s6 expects the process to stay running)
exec su - postgres -c "/usr/lib/postgresql/17/bin/postgres -D '$PGDATA' -c listen_addresses='127.0.0.1' -c port=5432"
