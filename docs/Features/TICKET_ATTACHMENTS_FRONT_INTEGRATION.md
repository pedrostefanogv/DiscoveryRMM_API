# Ticket Attachments - Frontend Integration

Este documento descreve como integrar upload de arquivos em tickets com URL preassinada e object storage S3-compatível.

## 1) Configuração global de Object Storage (Server)

Endpoint para leitura e atualização da configuração global:

- GET /api/configurations/server
- PUT /api/configurations/server
- PATCH /api/configurations/server

Campos relevantes no payload de server:

- objectStorageBucketName (string)
- objectStorageEndpoint (string)
- objectStorageRegion (string)
- objectStorageAccessKey (string)
- objectStorageSecretKey (string)
- objectStorageUrlTtlHours (int)
- objectStorageUsePathStyle (bool)
- objectStorageSslVerify (bool)

Exemplo de atualização (PUT /api/configurations/server):

{
  "objectStorageBucketName": "discovery-files",
  "objectStorageEndpoint": "https://s3.us-east-1.amazonaws.com",
  "objectStorageRegion": "us-east-1",
  "objectStorageAccessKey": "YOUR_ACCESS_KEY",
  "objectStorageSecretKey": "YOUR_SECRET_KEY",
  "objectStorageUrlTtlHours": 24,
  "objectStorageUsePathStyle": false,
  "objectStorageSslVerify": true
}

Observações:

- O backend usa sempre implementação MinIO (S3-compatível).
- Não existe mais seleção de provider via payload (`objectStorageProviderType`).
- Para modo ativo, basta configurar bucket/endpoint/região/chaves; sem isso, a validação falha.

## 2) Configuração de anexos de tickets

Endpoints:

- GET /api/configurations/server/ticket-attachments
- PUT /api/configurations/server/ticket-attachments

Contrato de TicketAttachmentSettings:

- enabled (bool)
- maxFileSizeBytes (long)
- allowedContentTypes (string[])
- presignedUploadUrlTtlMinutes (int)

Defaults:

- enabled = true
- maxFileSizeBytes = 10485760 (10 MB)
- allowedContentTypes = image/jpeg, image/png, image/webp, application/pdf
- presignedUploadUrlTtlMinutes = 15

Validação:

- maxFileSizeBytes > 0 e <= 1 GB
- presignedUploadUrlTtlMinutes entre 1 e 120
- allowedContentTypes com ao menos 1 item válido

Exemplo de configuração:

{
  "enabled": true,
  "maxFileSizeBytes": 10485760,
  "allowedContentTypes": [
    "image/jpeg",
    "image/png",
    "image/webp",
    "application/pdf"
  ],
  "presignedUploadUrlTtlMinutes": 15
}

## 3) Fluxo de upload direto para Ticket (Frontend)

### 3.1 Preparar upload

POST /api/tickets/{ticketId}/attachments/presigned-upload

Request:

{
  "fileName": "evidencia.pdf",
  "contentType": "application/pdf",
  "sizeBytes": 245760
}

Response 200:

{
  "attachmentId": "GUID",
  "objectKey": "clients/{clientIdN}/ticket/{ticketIdN}/attachments/{attachmentIdN}/evidencia.pdf",
  "uploadUrl": "https://...",
  "httpMethod": "PUT",
  "expiresAtUtc": "2026-03-12T18:35:00Z"
}

### 3.2 Upload do binário no storage

Executar PUT direto para uploadUrl retornada.

Headers recomendados:

- Content-Type: mesmo valor enviado no passo 3.1

Body:

- bytes do arquivo

### 3.3 Confirmar upload

POST /api/tickets/{ticketId}/attachments/complete-upload

Request:

{
  "attachmentId": "GUID",
  "objectKey": "clients/{clientIdN}/ticket/{ticketIdN}/attachments/{attachmentIdN}/evidencia.pdf",
  "fileName": "evidencia.pdf",
  "contentType": "application/pdf",
  "sizeBytes": 245760,
  "uploadedBy": "usuario@empresa.com"
}

Response 201:

- Retorna entidade Attachment persistida.

## 4) Listagem de anexos do ticket

GET /api/tickets/{ticketId}/attachments

Response 200:

- Lista de Attachment.

Campos principais de Attachment:

- id
- entityType
- entityId
- clientId
- fileName
- storageObjectKey
- storageBucket
- contentType
- sizeBytes
- storageChecksum
- storageProviderType
- uploadedBy
- createdAt
- updatedAt

## 5) Regras que o frontend deve respeitar

- Bloquear upload se enabled = false.
- Validar contentType e sizeBytes antes de chamar prepare.
- Sempre enviar no complete exatamente os mesmos attachmentId, objectKey, fileName, contentType e sizeBytes do upload realizado.
- O backend valida prefixo do objectKey para o ticket/cliente e rejeita se não corresponder.
- Se o upload direto falhar (PUT), não chamar complete-upload.

## 6) Erros esperados

Prepare/Complete podem retornar 400 com:

- Upload desabilitado
- Tipo MIME não permitido (retorna allowedContentTypes)
- Arquivo acima do limite (retorna maxFileSizeBytes)
- Campos obrigatórios ausentes
- ObjectKey inválido para o ticket

Not Found:

- ticketId inexistente retorna 404.

## 7) Sequência recomendada de UI

1. Carregar GET /api/configurations/server/ticket-attachments ao abrir tela de ticket.
2. Exibir file picker com filtros de MIME/size conforme settings.
3. Para cada arquivo: prepare -> PUT no uploadUrl -> complete.
4. Atualizar lista chamando GET /api/tickets/{ticketId}/attachments.
5. Mostrar estados por arquivo (preparando, enviando, confirmando, concluído/erro).
