#!/bin/bash
# Initialize Temporal databases and schema

set -e

# Create databases (idempotent using bash)
createdb -U postgres temporal 2>/dev/null || true
createdb -U postgres temporal_visibility 2>/dev/null || true

# Create schema in temporal database
psql -U postgres -d temporal -v ON_ERROR_STOP=1 <<'EOF'
    CREATE TABLE IF NOT EXISTS schema_version (
        version_partition INTEGER NOT NULL,
        db_name VARCHAR(255) NOT NULL,
        creation_time TIMESTAMP NOT NULL,
        curr_version INTEGER NOT NULL,
        min_compatible_version INTEGER NOT NULL,
        PRIMARY KEY(version_partition)
    );

    CREATE TABLE IF NOT EXISTS cluster_metadata_info (
        metadata_partition INTEGER NOT NULL,
        cluster_name VARCHAR(255),
        data BYTEA,
        data_encoding VARCHAR(255),
        version BIGINT DEFAULT 0,
        PRIMARY KEY(metadata_partition)
    );

    INSERT INTO schema_version (version_partition, db_name, creation_time, curr_version, min_compatible_version) 
    VALUES (0, 'temporal', NOW(), 119, 119) ON CONFLICT (version_partition) DO NOTHING;
EOF

# Create schema in temporal_visibility database
psql -U postgres -d temporal_visibility -v ON_ERROR_STOP=1 <<'EOF'
    CREATE TABLE IF NOT EXISTS schema_version (
        version_partition INTEGER NOT NULL,
        db_name VARCHAR(255) NOT NULL,
        creation_time TIMESTAMP NOT NULL,
        curr_version INTEGER NOT NULL,
        min_compatible_version INTEGER NOT NULL,
        PRIMARY KEY(version_partition)
    );

    INSERT INTO schema_version (version_partition, db_name, creation_time, curr_version, min_compatible_version) 
    VALUES (0, 'temporal_visibility', NOW(), 119, 119) ON CONFLICT (version_partition) DO NOTHING;
EOF

echo "Temporal database schema initialized successfully"

echo "Temporal databases initialized successfully"

echo "Temporal databases and schema initialized successfully"
