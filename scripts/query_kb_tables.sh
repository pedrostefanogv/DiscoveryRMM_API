#!/bin/bash
export PGPASSWORD=D1sc0v3ryH0m0l0g2025
PSQL="psql -h 127.0.0.1 -p 5432 -U discovery_app -d discovery -t -A"

echo "=== TABELAS KB ==="
$PSQL -c "SELECT table_name FROM information_schema.tables WHERE table_schema='public' AND table_name ILIKE '%knowledge%' ORDER BY table_name;"

echo ""
echo "=== CHUNKS relacionadas ==="
$PSQL -c "SELECT table_name FROM information_schema.tables WHERE table_schema='public' AND table_name ILIKE '%chunk%' OR table_name ILIKE '%embed%' ORDER BY table_name;"
