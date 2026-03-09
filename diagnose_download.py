#!/usr/bin/env python3
"""
Script de diagnóstico para problemas no download de relatórios
"""
import requests
import json
import time
import sys

BASE_URL = "http://127.0.0.1:5299"

def diagnose():
    print("=" * 70)
    print("DIAGNÓSTICO DE DOWNLOAD DE RELATÓRIOS - MEDUZA")
    print("=" * 70)
    
    # 1. Verificar saúde da API
    print("\n[1] Verificando saúde da API...")
    try:
        response = requests.get(f"{BASE_URL}/health", timeout=5)
        print(f"    ✓ Status: {response.status_code}")
    except Exception as e:
        print(f"    ✗ ERRO: {e}")
        return
    
    # 2. Listar templates
    print("\n[2] Listando templates disponíveis...")
    try:
        response = requests.get(f"{BASE_URL}/api/reports/templates", timeout=10)
        if response.status_code == 200:
            templates = response.json() if isinstance(response.json(), list) else response.json().get('value', [])
            if templates:
                template = templates[0]
                print(f"    ✓ Templates encontrados: {len(templates)}")
                print(f"      Template ID: {template['id']}")
                print(f"      Nome: {template['name']}")
                print(f"      Dataset: {template.get('datasetType', 'N/A')}")
                template_id = template['id']
            else:
                print(f"    ✗ Nenhum template encontrado")
                return
        else:
            print(f"    ✗ Erro: {response.status_code}")
            print(f"       Response: {response.text[:300]}")
            return
    except Exception as e:
        print(f"    ✗ ERRO: {e}")
        return
    
    # 3. Executar relatório
    print("\n[3] Executando relatório (runAsync=true)...")
    try:
        payload = {
            "templateId": template_id,
            "format": "Pdf",
            "runAsync": True
        }
        response = requests.post(
            f"{BASE_URL}/api/reports/run",
            json=payload,
            timeout=60,
            headers={"Content-Type": "application/json"}
        )
        
        print(f"    Status Code: {response.status_code}")
        print(f"    Response Body: {response.text[:500]}")
        
        if response.status_code in [200, 202]:
            execution = response.json()
            execution_id = execution.get('executionId')
            print(f"    ✓ Execution ID: {execution_id}")
        else:
            print(f"    ✗ Erro inesperado")
            return
    except Exception as e:
        print(f"    ✗ ERRO: {e}")
        return
    
    # 4. Aguardar conclusão
    print(f"\n[4] Aguardando conclusão (até 120 segundos)...")
    execution = None
    for attempt in range(60):
        try:
            response = requests.get(
                f"{BASE_URL}/api/reports/executions/{execution_id}",
                timeout=10
            )
            if response.status_code == 200:
                execution = response.json()
                status = execution.get('status')
                print(f"    [{attempt+1}/60] Status: {status}", end='\r')
                
                if status == "Completed":
                    print(f"\n    ✓ Concluído!")
                    print(f"      Tamanho: {execution.get('resultSizeBytes', 'N/A')} bytes")
                    print(f"      Content-Type: {execution.get('resultContentType', 'N/A')}")
                    print(f"      Caminho: {execution.get('resultPath', 'N/A')}")
                    break
                elif status == "Failed":
                    print(f"\n    ✗ Falhou!")
                    print(f"      Erro: {execution.get('errorMessage')}")
                    return
                
                time.sleep(2)
            else:
                print(f"\n    ✗ Erro ao verificar status: {response.status_code}")
                return
        except Exception as e:
            print(f"\n    ✗ ERRO: {e}")
            return
    
    if execution is None or execution.get('status') != "Completed":
        print(f"    ✗ Timeout - relatório não concluído a tempo")
        return
    
    # 5. Verificar arquivo no disco
    result_path = execution.get('resultPath')
    print(f"\n[5] Verificando arquivo no disco...")
    try:
        import os
        if os.path.exists(result_path):
            size = os.path.getsize(result_path)
            print(f"    ✓ Arquivo existe!")
            print(f"      Caminho: {result_path}")
            print(f"      Tamanho: {size} bytes")
        else:
            print(f"    ✗ Arquivo NÃO encontrado no disco!")
            print(f"      Caminho esperado: {result_path}")
            return
    except Exception as e:
        print(f"    ⚠ Não foi possível verificar sistema de arquivos: {e}")
    
    # 6. Tentar download - v1 (GetDownloadAsync)
    print(f"\n[6] Tentando download via /download...")
    try:
        response = requests.get(
            f"{BASE_URL}/api/reports/executions/{execution_id}/download",
            timeout=30
        )
        print(f"    Status Code: {response.status_code}")
        print(f"    Content-Length: {len(response.content)} bytes")
        print(f"    Content-Type: {response.headers.get('Content-Type', 'N/A')}")
        print(f"    Content-Disposition: {response.headers.get('Content-Disposition', 'N/A')}")
        
        if response.status_code == 200:
            print(f"    ✓ Download bem-sucedido!")
            # Tentar salvar arquivo
            try:
                with open("test_download.pdf", "wb") as f:
                    f.write(response.content)
                print(f"    ✓ Arquivo salvo como: test_download.pdf")
            except Exception as e:
                print(f"    ⚠ Erro ao salvar arquivo: {e}")
        else:
            print(f"    ✗ Erro no download")
            print(f"    Response: {response.text[:500]}")
            
            # Verificar se é um JSON error
            try:
                error_data = response.json()
                print(f"    Erro JSON: {json.dumps(error_data, indent=2)}")
            except:
                pass
    except Exception as e:
        print(f"    ✗ ERRO: {e}")
    
    # 7. Tentar download - v2 (stream)
    print(f"\n[7] Tentando download via /download-stream...")
    try:
        response = requests.get(
            f"{BASE_URL}/api/reports/executions/{execution_id}/download-stream",
            timeout=30
        )
        print(f"    Status Code: {response.status_code}")
        print(f"    Content-Length: {len(response.content)} bytes")
        
        if response.status_code == 200:
            print(f"    ✓ Stream download bem-sucedido!")
        else:
            print(f"    ✗ Erro no stream download")
            print(f"    Response: {response.text[:500]}")
    except Exception as e:
        print(f"    ✗ ERRO: {e}")
    
    print("\n" + "=" * 70)
    print("FIM DO DIAGNÓSTICO")
    print("=" * 70)

if __name__ == "__main__":
    diagnose()
