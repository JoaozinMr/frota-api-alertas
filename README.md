# Frota – API de Alertas

API REST (.NET 8) que recebe os alertas críticos do [Worker de Processamento](README_worker.md) e os expõe para painéis (Grafana / dashboard web). Parte do sistema distribuído de monitoramento de frota (ver [Contexto_Geral_Projeto_Frota.md](Contexto_Geral_Projeto_Frota.md), [Plano_LLM_API_Alertas.md](Plano_LLM_API_Alertas.md) e [Handoff_Worker_para_API_Alertas.md](Handoff_Worker_para_API_Alertas.md)).

## Funcionalidades

| Requisito | Implementação |
|---|---|
| API REST | [AlertaEndpoints](src/FrotaAlertas/Services/AlertaEndpoints.cs) (Minimal API) |
| Validação | [AlertaValidator](src/FrotaAlertas/Services/AlertaValidator.cs): `400` só para payload realmente inválido |
| Persistência | [PostgresAlertaRepository](src/FrotaAlertas/Services/AlertaRepository.cs): tabela própria `alertas` no banco `frota`, criada na inicialização com retry de 3s |
| Idempotência | `UNIQUE (id_veiculo, tipo_infracao, timestamp)` + `ON CONFLICT DO NOTHING`. Duplicata → `200` com o registro existente (nunca `409`) |
| Latência | Orçamento de 2s por POST (`Alertas:RequestTimeoutMs`), abaixo do timeout de 3s do Worker. Se estourar → `503` |
| Correlation ID | [CorrelationIdMiddleware](src/FrotaAlertas/Services/CorrelationId.cs): header `X-Correlation-ID` → `correlation_id` do body → GUID. Vai para o log, o header de resposta e o banco |
| Logs | Serilog JSON (`RenderedCompactJsonFormatter`), `Service=frota-api-alertas`, propriedades `CorrelationId`, `IdVeiculo`, `Tipo` |
| Health check | `GET /health` ([PostgresHealthCheck](src/FrotaAlertas/Services/PostgresHealthCheck.cs): schema criado + `SELECT 1`) |
| Container | [Dockerfile](Dockerfile) multi-stage, `USER app`, `HEALTHCHECK` via `bash /dev/tcp` |

## Endpoints

| Método | Rota | Descrição |
|---|---|---|
| `POST` | `/api/alerts` | Recebe alerta do Worker. `201` novo, `200` duplicata, `400` inválido, `503` banco indisponível |
| `GET` | `/api/alerts` | Lista alertas. Filtros: `id_veiculo`, `tipo_infracao`, `desde`, `ate`, `limit` (padrão 100, máx. 1000) |
| `GET` | `/api/alerts/{id}` | Alerta por id |
| `GET` | `/api/alerts/resumo` | Totais por tipo e top veículos. Filtros: `desde`, `ate`, `top` (padrão 10) |
| `GET` | `/health` | Saúde da API e do PostgreSQL |

Payload do POST (snake_case; `latitude`, `longitude`, `velocidade` e `correlation_id` podem ser `null`):

```json
{
  "id_veiculo": "V-001",
  "tipo_infracao": "EXCESSO_VELOCIDADE",
  "descricao": "Velocidade 92.3 km/h excede o limite de 80 km/h",
  "latitude": -23.55,
  "longitude": -46.63,
  "velocidade": 92.3,
  "timestamp": "2026-10-10T12:00:00+00:00",
  "correlation_id": "6f1c..."
}
```

## Configuração

Em `appsettings.json` ou variáveis de ambiente: `Postgres__ConnectionString`, `Postgres__SchemaRetrySeconds`, `Alertas__RequestTimeoutMs`, `Alertas__DefaultLimit`, `Alertas__MaxLimit`.

Porta local: `5100` (padrão do Worker, `Alertas__BaseUrl=http://localhost:5100`). No container: `8080`. No Docker Compose, configure o Worker com `Alertas__BaseUrl=http://<servico-alertas>:8080`.

## Executar

```bash
dotnet test
dotnet run --project src/FrotaAlertas
docker build -t frota-api-alertas .
docker run -p 5100:8080 -e Postgres__ConnectionString="Host=host.docker.internal;Port=5432;Database=frota;Username=frota;Password=frota" frota-api-alertas
```

Teste rápido:

```bash
curl -i -X POST http://localhost:5100/api/alerts \
  -H "Content-Type: application/json" -H "X-Correlation-ID: teste-123" \
  -d '{"id_veiculo":"V-001","tipo_infracao":"SENSOR_MORTO","descricao":"Sem sinal","latitude":null,"longitude":null,"velocidade":null,"timestamp":"2026-10-10T12:00:00Z","correlation_id":null}'
curl "http://localhost:5100/api/alerts?id_veiculo=V-001&limit=10"
```