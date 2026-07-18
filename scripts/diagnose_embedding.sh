#!/bin/bash
export PGPASSWORD=D1sc0v3ryH0m0l0g2025

echo "=== CREDENCIAIS AI ==="
psql -h 127.0.0.1 -p 5432 -U discovery_app -d discovery -c "SELECT id, scope_type, provider, LEFT(api_key_encrypted,30) as key_prefix, CASE WHEN embedding_api_key_encrypted IS NULL OR embedding_api_key_encrypted = '' THEN 'VAZIO' ELSE 'PRESENTE' END as has_emb_key FROM ai_provider_credentials;"

echo ""
echo "=== AI SETTINGS (site_config) ==="
psql -h 127.0.0.1 -p 5432 -U discovery_app -d discovery -c "SELECT site_id, key, LEFT(value, 100) FROM site_configurations WHERE key ILIKE '%ai%' OR key ILIKE '%embed%' LIMIT 15;" 2>/dev/null

echo ""
echo "=== SERVER CONFIG (CurrentEmbeddingDimensions) ==="
psql -h 127.0.0.1 -p 5432 -U discovery_app -d discovery -c "SELECT current_embedding_dimensions, updated_at, updated_by FROM server_configurations LIMIT 1;"

echo ""
echo "=== CHUNKS COM EMBEDDING ==="
psql -h 127.0.0.1 -p 5432 -U discovery_app -d discovery -c "SELECT COUNT(*) as total, COUNT(*) FILTER (WHERE embedding IS NOT NULL) as with_emb FROM knowledge_article_chunks;"
