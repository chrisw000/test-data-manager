# TDM Grafana pack (W3-P4)

Provisioning-ready dashboard + starter alert rules for the `Tdm` OTEL metrics, so teams
import instead of rebuilding. Assumes the usual pipeline:

```
tdm run (OTLP) → OTEL collector → Prometheus → Grafana
```

TDM exports OTLP when `OTEL_EXPORTER_OTLP_ENDPOINT` is set (see `Tdm.Observability`).
Prometheus metric names below come from the standard collector/prometheus naming of the
`Tdm` meter's instruments:

| Instrument | Prometheus name | Labels |
|---|---|---|
| `tdm.entities.created` | `tdm_entities_created_total` | `entity` |
| `tdm.entities.updated` | `tdm_entities_updated_total` | `entity` |
| `tdm.entities.deleted` | `tdm_entities_deleted_total` | `entity` |
| `tdm.entities.failed` | `tdm_entities_failed_total` | — |
| `tdm.step.duration` (ms) | `tdm_step_duration_milliseconds_*` | `verb` |
| `tdm.persist.duration` (ms) | `tdm_persist_duration_milliseconds_*` | `entity`, `verb`, `route` |

## Dashboard

`tdm-dashboard.json` — import via **Dashboards → New → Import** (you'll be prompted for
the Prometheus datasource), or provision it:

```yaml
# /etc/grafana/provisioning/dashboards/tdm.yaml
apiVersion: 1
providers:
  - name: tdm
    folder: TDM
    type: file
    options: { path: /var/lib/grafana/dashboards/tdm }
```

Panels: created/updated/deleted/failed totals, create throughput by entity, failure ratio,
and p95 step/persist durations by verb, route and entity.

## Alerts

`tdm-alerts.yaml` — Grafana unified-alerting provisioning. Replace `<PROMETHEUS_UID>` with
your datasource UID and drop the file into `/etc/grafana/provisioning/alerting/`.

Two starter rules, deliberately conservative:

- **TDM entities failing** — any persist failure over 5 minutes.
- **TDM persist p95 above budget** — p95 over 250 ms for 10 minutes; set the number from
  your own `tdm bench tune` / trend-store history, and mirror it as a policy perf gate
  (W3-D8) so CI fails before the dashboard turns red.
