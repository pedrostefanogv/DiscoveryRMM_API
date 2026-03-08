#!/usr/bin/env python3
"""
Script para testar geração de relatórios em PDF via API Meduza
"""
import requests
import json
import time
import sys

BASE_URL = "http://127.0.0.1:5299"

def test_api_health():
    """Verifica se a API está respondendo"""
    try:
        response = requests.get(f"{BASE_URL}/health", timeout=5)
        print(f"✓ API Health: {response.status_code}")
        return True
    except Exception as e:
        print(f"✗ API Health: {e}")
        return False

def list_templates():
    """Lista todos os templates disponíveis"""
    try:
        response = requests.get(f"{BASE_URL}/api/reports/templates", timeout=10)
        if response.status_code == 200:
            data = response.json()
            # API retorna um array direto, não um objeto com 'value'
            templates = data if isinstance(data, list) else data.get('value', [])
            print(f"\n✓ Templates encontrados: {len(templates)}")
            for i, template in enumerate(templates[:3]):
                print(f"  [{i+1}] {template['name']} (Format: {template.get('defaultFormat', 'N/A')})")
                print(f"      ID: {template['id']}")
                print(f"      Dataset: {template.get('datasetType', 'N/A')}")
            return templates
        else:
            print(f"✗ Erro ao listar templates: {response.status_code}")
            print(response.text[:500])
            return []
    except Exception as e:
        print(f"✗ Erro ao listar templates: {e}")
        return []

def execute_report(template_id, format_type="Pdf"):
    """Executa um relatório"""
    try:
        payload = {
            "templateId": template_id,
            "format": format_type,
            "runAsync": True  # Fila para processamento assíncrono
        }
        print(f"\n→ Executando relatório:")
        print(f"  TemplateId: {template_id}")
        print(f"  Format: {format_type}")
        print(f"  RunAsync: True")
        
        response = requests.post(
            f"{BASE_URL}/api/reports/run",  # ENDPOINT CORRETO
            json=payload,
            timeout=60,
            headers={"Content-Type": "application/json"}
        )
        
        print(f"  Status Code: {response.status_code}")
        
        if response.status_code == 202:
            data = response.json()
            exec_id = data.get('executionId', 'N/A')
            print(f"✓ Relatório enfileirado para processamento")
            print(f"  Execution ID: {exec_id}")
            return exec_id
        elif response.status_code == 200:
            data = response.json()
            print(f"✓ Relatório gerado com sucesso!")
            print(f"  Response: {json.dumps(data, indent=2)[:500]}")
            return data.get('executionId', 'N/A')
        else:
            print(f"✗ Erro ao executar relatório: {response.status_code}")
            print(f"  Response: {response.text[:500]}")
            return None
    except Exception as e:
        print(f"✗ Erro ao executar relatório: {e}")
        return None

def check_execution_status(execution_id):
    """Verifica o status de uma execução"""
    try:
        response = requests.get(
            f"{BASE_URL}/api/reports/executions/{execution_id}",
            timeout=10
        )
        if response.status_code == 200:
            data = response.json()
            status = data.get('status', 'Unknown')
            print(f"\n✓ Status da execução:")
            print(f"  ID: {execution_id}")
            print(f"  Status: {status}")
            print(f"  Tamanho: {data.get('resultSizeBytes', 'N/A')} bytes")
            print(f"  Tipo: {data.get('resultContentType', 'N/A')}")
            return data
        else:
            print(f"✗ Erro ao verificar status: {response.status_code}")
            return None
    except Exception as e:
        print(f"✗ Erro ao verificar status: {e}")
        return None

def download_report(execution_id, output_file):
    """Baixa o relatório gerado"""
    try:
        response = requests.get(
            f"{BASE_URL}/api/reports/executions/{execution_id}/download",
            timeout=30
        )
        if response.status_code == 200:
            with open(output_file, 'wb') as f:
                f.write(response.content)
            print(f"\n✓ Relatório baixado com sucesso!")
            print(f"  Arquivo: {output_file}")
            print(f"  Tamanho: {len(response.content)} bytes")
            return True
        else:
            print(f"✗ Erro ao baixar relatório: {response.status_code}")
            return False
    except Exception as e:
        print(f"✗ Erro ao baixar relatório: {e}")
        return False

def main():
    print("=" * 60)
    print("Teste de Geração de Relatórios em PDF - Meduza API")
    print("=" * 60)
    
    # Aguarda a API ficar pronta
    print("\n[1] Aguardando API inicializar...")
    for i in range(30):
        if test_api_health():
            break
        if i < 29:
            time.sleep(1)
    else:
        print("✗ API não respondeu")
        sys.exit(1)
    
    # Lista templates
    print("\n[2] Listando templates...")
    templates = list_templates()
    
    if not templates:
        print("✗ Nenhum template disponível")
        sys.exit(1)
    
    # Tenta um template com formato Logs
    template_to_use = next((t for t in templates if t.get('datasetType') == 'Logs'), templates[0])
    template_id = template_to_use['id']
    
    print(f"\n[3] Usando template: {template_to_use['name']}")
    
    # Executa relatório
    print("\n[4] Executando relatório...")
    exec_id = execute_report(template_id, "Pdf")
    
    if not exec_id:
        print("✗ Falha ao executar relatório")
        sys.exit(1)
    
    # Aguarda e verifica status
    print("\n[5] Aguardando processamento...")
    for i in range(30):
        time.sleep(2)
        status_data = check_execution_status(exec_id)
        
        if not status_data:
            continue
            
        status = status_data.get('status', 'Unknown')
        
        if status == "Completed":
            print(f"✓ Relatório completado com sucesso!")
            
            # Tenta baixar
            print("\n[6] Baixando relatório...")
            output_file = f"c:\\Projetos\\SRV_Meduza_2\\report_test_{exec_id[:8]}.pdf"
            if download_report(exec_id, output_file):
                print("\n" + "=" * 60)
                print("✓ TESTE CONCLUÍDO COM SUCESSO!")
                print("=" * 60)
            else:
                print("\n✗ Falha ao baixar relatório")
            break
        elif status == "Failed":
            error_msg = status_data.get('errorMessage', 'Desconhecido')
            print(f"✗ Relatório falhou: {error_msg}")
            break
        else:
            print(f"  → Status atual: {status} ({i+1}/30)")
    else:
        print("✗ Timeout aguardando processamento")

if __name__ == "__main__":
    main()
