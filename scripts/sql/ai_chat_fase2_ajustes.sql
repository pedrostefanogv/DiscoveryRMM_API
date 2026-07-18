-- ============================================================================
-- Script de ajuste de configuração AI Chat — Fase 2
-- Executar no servidor: PGPASSWORD=... psql -h 127.0.0.1 -U discovery_app -d discovery -f script.sql
-- ============================================================================

-- 1. Desabilitar tools sem handler implementado
UPDATE mcp_tool_policies SET is_enabled = false 
WHERE tool_name IN ('filesystem.read_file', 'postgres.query');

-- 2. Ajustar MaxToolCallIterations para 2 (default já foi reduzido no código, 
--    mas ajustar o valor no DB para quem tem override)
-- (nota: o campo está em AIIntegrationSettings que é resolvido via ConfigurationResolver, 
--  não tem tabela fixa. Se houver configuração por site, ajustar manualmente)

-- 3. Verificar base de conhecimento
SELECT 'knowledge_chunks' AS tabela, COUNT(*) AS total FROM knowledge_chunks
UNION ALL
SELECT 'knowledge_articles (published)', COUNT(*) FROM knowledge_articles WHERE status = 'published';

-- 4. Verificar mensagens com respostas vazias (problema 1.2)
SELECT COUNT(*) AS empty_responses, 
       COUNT(*) * 100.0 / NULLIF((SELECT COUNT(*) FROM ai_chat_messages WHERE role = 'assistant'), 0) AS pct_empty
FROM ai_chat_messages 
WHERE role = 'assistant' AND (content IS NULL OR content = '' OR TRIM(content) = '');

-- 5. Verificar distribuição de latência
SELECT 
    CASE 
        WHEN latency_ms < 3000 THEN '<3s'
        WHEN latency_ms < 10000 THEN '3-10s'
        WHEN latency_ms < 20000 THEN '10-20s'
        ELSE '>20s'
    END AS faixa_latencia,
    COUNT(*) AS total
FROM ai_chat_messages 
WHERE role = 'assistant' AND latency_ms IS NOT NULL
GROUP BY 1
ORDER BY MIN(latency_ms);

-- 6. Modelos usados
SELECT model_version, COUNT(*) AS total, AVG(latency_ms)::INT AS avg_latency_ms
FROM ai_chat_messages 
WHERE role = 'assistant' AND model_version IS NOT NULL
GROUP BY model_version
ORDER BY total DESC;
