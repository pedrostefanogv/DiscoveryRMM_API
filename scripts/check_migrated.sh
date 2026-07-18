#!/bin/bash
export PGPASSWORD=D1sc0v3ryH0m0l0g2025
PSQL="psql -h 127.0.0.1 -p 5432 -U discovery_app -d discovery -t -A"

echo "=== CAMPOS FEEDBACK ==="
$PSQL -c "SELECT column_name, data_type FROM information_schema.columns WHERE table_name='ai_chat_messages' AND column_name IN ('feedback_score','feedback_comment') ORDER BY ordinal_position;"

echo ""
echo "=== ARTIGOS KB ==="
$PSQL -c "SELECT COUNT(*) as total_articles, COUNT(*) FILTER (WHERE status='published') as published FROM knowledge_articles;"

echo ""
echo "=== CHUNKS COM EMBEDDING ==="
$PSQL -c "SELECT COUNT(*) as total, COUNT(*) FILTER (WHERE embedding IS NOT NULL) as with_emb FROM knowledge_article_chunks;"

echo ""
echo "=== EMBEDDING QUEUE ==="
$PSQL -c "SELECT status, COUNT(*) FROM knowledge_embedding_queue GROUP BY status;"
