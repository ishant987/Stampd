<div align="center">

<img src="docs/banner.svg" alt="Stampd — Open-source PDF signing for the .NET ecosystem" />

<br/>

[![License: Apache 2.0](https://img.shields.io/badge/License-Apache_2.0-0b3c6e?style=flat-square)](https://opensource.org/licenses/Apache-2.0)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-1a5698?style=flat-square&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![PAdES B-LT](https://img.shields.io/badge/PAdES-B--LT-2b7fce?style=flat-square)](https://en.wikipedia.org/wiki/PAdES)
[![Adobe Verified](https://img.shields.io/badge/Adobe%20Acrobat-verified%20signature-1b7a3a?style=flat-square&logo=adobe&logoColor=white)](#)
[![Status: v3.0](https://img.shields.io/badge/status-v3.0-1a5698?style=flat-square)](#release-history)

**An executive-grade, open-source e-signature platform built natively for .NET.**

[Quick start](#quick-start) · [Architecture](#architecture) · [Documentation](#documentation) · [Roadmap](#roadmap) · [Contributing](#contributing)

</div>

---

## Why Stampd

The .NET ecosystem has world-class libraries for everything from data access (EF Core) to logging (Serilog) to background work (Hangfire) to resilience (Polly).

It does not have a production-grade open-source e-signature platform.

Today, every .NET shop that needs to embed signing in a product faces the same three bad options:

1. **Pay DocuSign per envelope** — $0.50–$5 per signed PDF. Scales linearly with your customers.
2. **Integrate something written in another stack** — DocuSeal (Ruby) and Documenso (TypeScript) are excellent, but neither is in your language, your package manager, or your tooling.
3. **Build it yourself from scratch** — months of PAdES, ASN.1, CMS, and BouncyCastle to get to a green check in Adobe Acrobat.

Stampd is the option that should have existed.

## Highlights

| | |
|---|---|
| **Cryptographically sound** | PAdES B-B, B-T, B-LT signatures verifiable in Adobe Acrobat. RFC 3161 timestamps. Embedded OCSP + CRL for long-term validity. |
| **HSM-first** | Pluggable sealing providers: local certificate, HashiCorp Vault, OpenBao, Azure Key Vault, AWS KMS. Private keys never leave the HSM. |
| **Multi-cloud storage** | Built-in adapters for local filesystem, S3, Azure Blob, and Google Cloud Storage — your choice, your tenant-prefix policy, your KMS keys. |
| **Multi-provider database** | SQL Server, PostgreSQL, SQLite. EF Core 10 throughout, with global tenant query filters and append-only audit. |
| **Truly open source** | Apache 2.0 across the entire stack. No relicensing clause. No "open-core" feature gate. The thing you fork is the thing we ship. |
| **Executive-grade UI** | Static SSR Blazor signer experience with real PDF.js rendering. Visual template designer. Dark/light theme. Zero dependencies on commercial component libraries. |
| **Production observability** | Serilog structured logging, OpenTelemetry traces + metrics, correlation IDs, real health probes, JWT auth, rate limiting. Ships in a Docker container. |
| **Webhook outbox** | HMAC-SHA256-signed delivery of lifecycle events with exponential-backoff retry. Atomic with workflow state changes via the outbox pattern. |

## Quick start

You need .NET 10 SDK installed. Everything else is bundled.

```bash
git clone https://github.com/isureshsubramanian/Stampd.git
cd Stampd

# Terminal A — the API
dotnet run --project src/Stampd.WebApi
# Listening on http://localhost:5070

# Terminal B — the UI
dotnet run --project src/Stampd.UI
# Listening on http://localhost:5170
```

The SQLite database, self-signed signing certificate, and migration apply automatically on first boot. Open <http://localhost:5170> to land in the designer, or jump straight to the API reference at <http://localhost:5070/scalar/v1>.

### Sign your first PDF in 60 seconds

```bash
BASE_URL=http://localhost:5070
TOKEN=$(curl -s -X POST $BASE_URL/api/auth/dev-token \
  -H "Content-Type: application/json" \
  -d '{"subject":"hello"}' | jq -r .accessToken | tr -d '\n')

# Convert a PDF on disk to base64 and POST it for one-shot signing.
PDF_B64=$(base64 -i your-document.pdf)

curl -s -X POST $BASE_URL/api/sign \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d "{\"sourcePdfBase64\":\"$PDF_B64\",\"fields\":[],\"fieldValues\":{}}" \
  | jq -r .signedPdfBase64 | base64 -d > signed.pdf

open signed.pdf
```

Adobe Acrobat opens the signed PDF with the signature panel populated, the byte range verified, and a yellow "signer's identity is unknown" badge (because we used a self-signed cert). Drag the `.cer` file into Adobe's trusted identities and the badge flips green.

## Architecture

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                                Stampd.UI                                    │
│                (Blazor Web App — signer + designer pages)                   │
└─────────────────────────────┬───────────────────────────────────────────────┘
                              │ HTTP + JWT
┌─────────────────────────────▼───────────────────────────────────────────────┐
│                              Stampd.WebApi                                  │
│  Templates · Signing Requests · Recipient Signing · Webhooks · Bulk Send    │
└──────┬──────────────────┬──────────────────┬───────────────┬────────────────┘
       │                  │                  │               │
┌──────▼──────┐   ┌───────▼───────┐   ┌──────▼──────┐  ┌─────▼─────────┐
│ Stampd      │   │ Stampd        │   │ Stampd      │  │  Stampd       │
│ Engine      │   │ Infrastructure│   │ Storage     │  │  Identity     │
│ (PdfSharp + │   │ (EF Core 10,  │   │ (Local FS,  │  │  (Email OTP,  │
│  BouncyCastle│  │  multi-DB)    │   │  S3, AzBlob,│  │   SMS OTP,    │
│  PAdES B-LT)│   │               │   │  GCS)       │  │   KBA)        │
└──────┬──────┘   └───────────────┘   └─────────────┘  └───────────────┘
       │
┌──────▼──────────────────────────────────────────────────────────────────────┐
│                          Sealing Providers                                  │
│  LocalCert · HashiCorp Vault · OpenBao · Azure Key Vault · AWS KMS          │
└─────────────────────────────────────────────────────────────────────────────┘
                              │
                              ▼
                  ┌───────────────────────┐
                  │  RFC 3161 TSA         │
                  │  (FreeTSA · DigiCert  │
                  │   · GlobalSign · …)   │
                  └───────────────────────┘
```

Every box is a separate project. Every diagonal arrow is an interface in `Stampd.Core` you can swap by changing one line of DI registration. Need a custom HSM? Implement `ICryptographicSealingProvider`. Custom storage? `IDocumentStorageProvider`. Custom identity verification? `IIdentityVerificationProvider`.

## Email

Workflow invitations and Email-OTP identity-verification codes go out through a single SMTP `IEmailSender` registered in `Stampd.WebApi` (backed by MailKit, MIT licensed).

Configure your SMTP transport via standard configuration:

```jsonc
// appsettings.Production.json
{
  "Stampd": {
    "Email": {
      "Smtp": {
        "Host": "email-smtp.us-east-1.amazonaws.com",
        "Port": 587,
        "Username": "AKIA…",
        "Password": "…",
        "Security": "StartTls"   // None | Auto | SslOnConnect | StartTls | StartTlsWhenAvailable
      }
    }
  }
}
```

Env-var form: `Stampd__Email__Smtp__Host`, `Stampd__Email__Smtp__Port`, etc.

## Documentation

| Document | Purpose |
|---|---|
| [`/scalar/v1` on the running WebApi](http://localhost:5070/scalar/v1) | Interactive OpenAPI reference. |


## Feature matrix

| Capability | v1.0 | v1.1 | v1.2 | v1.3 | v2.0 | v2.1 | v2.2 | v2.3 | v3.0 (current) |
|---|---|---|---|---|---|---|---|---|---|
| PAdES B-B (basic) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| PAdES B-T (RFC 3161 timestamp) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| PAdES B-LT (CRL + OCSP via DSS) | — | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| PAdES B-LTA (archive timestamp) | — | — | ✅ | ✅ † | ✅ † | ✅ † | ✅ † | ✅ † | ✅ † |
| Strict ETSI ATSHashIndexV3 imprint | — | — | — | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| PAdES Document Timestamp (`/Type /DocTimeStamp`) | — | — | — | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Local certificate sealing | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| HashiCorp Vault / OpenBao Transit | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Azure Key Vault | — | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| AWS KMS | — | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Configurable RFC 3161 TSA (DigiCert / GlobalSign / Sectigo / internal) | — | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Opt-in DigiCert TSA failover (resilience for FreeTSA outages) | — | — | — | — | — | ✅ | ✅ | ✅ | ✅ |
| SQL Server / Postgres / SQLite | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| S3 / Azure Blob / GCS storage | — | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Email-driven workflow dispatch | — | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Persistent OTP store (DB-backed) | — | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| SMS OTP + KBA identity verification | — | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| OTP rate limit + brute-force lockout | — | — | — | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Webhook outbox with HMAC delivery | — | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Bulk-send worker | — | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Multi-recipient field aggregation | — | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Multi-recipient sender detail page | — | — | — | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Sender completion notification email | — | — | — | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Blazor signer experience | — | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Blazor template designer | — | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Identity-verification UI gates | — | — | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Drag-to-move + resize handles in designer | — | — | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Transparent signature rendering (PNG alpha through PdfSharp) | — | — | — | — | — | ✅ | ✅ | ✅ | ✅ |
| True PDF incremental update for strict ETSI B-LT | — | — | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Paginated signing-requests list (UI + API) | — | — | — | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| RBAC: Admin / Sender / ReadOnly roles + policies | — | — | — | — | ✅ | ✅ | ✅ | ✅ | ✅ |
| Tenant-isolated admin scopes (`AdminScopes` + per-tenant gate) | — | — | — | — | — | — | — | — | ✅ |
| Real OIDC relay (Auth0 / Okta / Azure AD / Google / Keycloak) | — | — | — | — | — | — | — | — | ✅ |
| SAML2 SP scaffolding (config + endpoint surface; impl in v3.0.1) | — | — | — | — | — | — | — | — | ✅ |
| Industry compliance bundles (HIPAA / 21 CFR Part 11 / eIDAS QES) | — | — | — | — | — | — | — | — | ✅ |
| Production guard: `Mode=DevJwt` blocked outside Development | — | — | — | — | — | — | — | — | ✅ |
| Admin dashboard at `/admin` (summary + 30-day trend + top templates) | — | — | — | — | ✅ | ✅ | ✅ | ✅ | ✅ |
| Drop-off funnel + time-to-sign + identity-verification analytics | — | — | — | — | ✅ | ✅ | ✅ | ✅ | ✅ |
| Prior-window comparison on every analytics widget (▲/▼ deltas) | — | — | — | — | — | — | ✅ | ✅ | ✅ |
| Per-channel identity-verification breakdown (Email / SMS / KBA) | — | — | — | — | — | — | ✅ | ✅ | ✅ |
| Request-weighted funnel completion (multi-recipient fairness) | — | — | — | — | — | — | ✅ | ✅ | ✅ |
| Per-role time-to-sign segmentation (tenant-wide across templates) | — | — | — | — | — | — | ✅ | ✅ | ✅ |
| Weekday vs weekend funnel split (dispatch-time conversion gap) | — | — | — | — | — | — | — | ✅ | ✅ |
| Webhook delivery retry observability widget | — | — | — | — | — | — | — | ✅ | ✅ |
| Per-sender productivity dashboard | — | — | — | — | — | — | — | ✅ | ✅ |
| Audit-trail CSV export (streamed) | — | — | — | — | — | — | — | ✅ | ✅ |
| Signing-requests filters (status / sender / recipient / date) + sort | — | — | — | — | ✅ | ✅ | ✅ | ✅ | ✅ |
| Strict completed-date sort (V15 `CompletedAtUtcEpochMs` shadow column) | — | — | — | — | — | ✅ | ✅ | ✅ | ✅ |
| Shareable + back-button-safe filter URLs (query-string sync) | — | — | — | — | — | ✅ | ✅ | ✅ | ✅ |
| Admin bulk operations: void, resend invitation, demo cleanup | — | — | — | — | ✅ | ✅ | ✅ | ✅ | ✅ |
| Audit attribution (`ActorUserId` + `ActorRole` on every event) | — | — | — | — | ✅ | ✅ | ✅ | ✅ | ✅ |
| Theme persistence across navigation (light / dark) | — | — | — | — | ✅ | ✅ | ✅ | ✅ | ✅ |

† **B-LTA carrier — belt + suspenders.** Stampd emits BOTH long-term archive carriers for B-LTA, so adopters get maximum verifier interop:
>
> 1. **CMS `id-aa-ets-archiveTimestampV3` unsigned attribute** (OID `1.2.840.113549.1.9.16.2.48`) carrying a second TSA assertion alongside the strict ETSI **`id-aa-ats-hash-index-v3`** attribute (OID `1.2.840.113549.1.9.16.2.51`, RFC 7026 / ETSI TS 101 733 §6.4.3) — Part 2 era carrier. The hash index locks in which certs, CRLs, and existing unsigned attrs the archive TST witnessed, so future additions to the CMS can't silently invalidate the timestamp. **v1.3 #131** ✅ replaced the v1.2 pragmatic SignerInfo-DER imprint with this spec-compliant computation.
> 2. **PAdES Document Timestamp** (`/Type /DocTimeStamp` signature dictionary with `/SubFilter /ETSI.RFC3161`) — Part 4 carrier per ETSI EN 319 142-1 §5.4. Appended as a separate strictly-additive incremental update revision past the DSS, with its own `/ByteRange` covering the entire prior PDF. **v1.3 #132** ✅ adds this in parallel to the CMS carrier.
>
> Cost: B-LTA signing now makes **3 TSA round-trips** (signature TST + CMS archive TST + Document Timestamp). Adopters who only need short-term verifiability should target B-T (1 round-trip) or B-LT (1 round-trip + revocation gather).

## Roadmap

**v2.0 candidates** — RBAC foundation (Admin / Sender / ReadOnly roles + ASP.NET Core policies + role-gated nav) ✅, admin dashboard at `/admin` with summary tiles + 30-day trend + top-templates table ✅, drop-off funnel + per-template time-to-sign + identity-verification analytics ✅, signing-requests filter panel (status, sender, recipient, date range) + multi-column sort ✅, admin bulk operations (void, resend invitation, demo cleanup) ✅, audit attribution (`ActorUserId` + `ActorRole` on every event) + V14 migrations on SQLite / SqlServer / Postgres ✅, theme persistence across navigation ✅.

**v2.1 candidates** — transparent signature rendering (PdfSharp PNG alpha normalization via SkiaSharp re-encode, #213) ✅, V15 `CompletedAtUtcEpochMs` shadow column on SigningRequest so the "completed" sort no longer proxies through `CreatedAtUtcEpochMs` (#212) ✅, URL query-string sync for shareable + back-button-safe filters on the signing-requests list (#196) ✅, opt-in DigiCert TSA failover wrapper for resilience against FreeTSA outages ✅.

**v2.2 candidates** — previous-window comparison on every analytics widget (current vs prior period delta with green ▲ / red ▼ chips) ✅, per-channel identity-verification breakdown (Email / SMS / KBA stacked bars for initiates + lockouts so admins see which method is being brute-forced) ✅, multi-recipient funnel weighting (each request contributes `signed ÷ recipients` so a 2-of-3-signed request scores 0.67 instead of 0) ✅, per-role time-to-sign segmentation (aggregates across templates by `Recipient.Role.Name` so admins answer "do Approvers always take longer than Signers?" tenant-wide) ✅.

**v2.3 candidates** — webhook delivery retry observability widget (queue depth + recent failures table on `/admin` reading from `WebhookEndpoint.ConsecutiveFailures` + `WebhookDelivery.LastErrorMessage`) ✅, recipient-funnel weekday vs weekend split (dispatch-time conversion gap so admins know whether to batch dispatches to weekday mornings) ✅, per-sender productivity dashboard (dispatched / completed / voided + avg-time-to-sign per `CreatedBy`) ✅, audit-trail export to CSV (streamed `GET /api/admin/audit/export` with date / event-type / actor filters, configurable `Stampd:Admin:MaxAuditExportRows` cap) ✅.

**v3.0 shipped** — tenant-isolated admin scopes (alpha.1) ✅, real OIDC relay against external IdPs (alpha.2) ✅, SAML2 SP scaffolding with eager config validation (alpha.3, 501 stubs until v3.0.1) ✅, industry compliance bundles (HIPAA / 21 CFR Part 11 / eIDAS QES) with runtime gates (stable) ✅. Auth-mode startup guard blocks `DevJwt` outside Development.

**v3.1 candidates** — workflow rules engine (conditional fields + branching DSL + designer UI, the headline deferred from v3.0), persistent `SigningRequest.ComplianceBundle` column + V17 migration, SAML2 SP production wiring (ITfoxtec.Identity.Saml2), maintenance worker that prunes audit rows per bundle retention window, IV challenge latency p50/p95, audit-trail Parquet export.

## Release history

- **v3.0.0** *(current)* — MAJOR release. **Tenant-isolated admin scopes**: `AdminScopes` table (V16 migration on every provider) + new auth handler that requires both the `Admin` role claim AND an active scope row for the request's tenant + `POST/DELETE/GET /api/admin/scopes` endpoints + `Stampd:Auth:SuperAdminUserIds` bootstrap config. **Real SSO**: `Stampd.Identity.Oidc` validates JWTs from any OpenID Connect IdP (Auth0, Okta, Azure AD, Google, Keycloak) via JWKS auto-discovery, with explicit-opt-in `RoleClaimMappings` translating external groups to Stampd's role taxonomy. **SAML2 SP scaffolding**: `Stampd.Identity.Saml2` with eager config validation, claim mapper, and mapped endpoints (`/api/auth/saml/{metadata,login,acs}`) returning 501 until v3.0.1 wires real assertion validation. **Compliance bundles**: `Stampd.Compliance` adds `ComplianceBundle` enum (Hipaa / Cfr21Part11 / EidasQes / None) with runtime `IComplianceGate` enforcing signature-level floor + IV requirement + QES marker check. **Production guard**: `Stampd:Auth:Mode=DevJwt` throws at startup outside Development/Testing environments — preventing v2.x dev-minter accidents in production. **Workflow rules engine deferred to v3.1**. Detailed v2.3 → v3.0 upgrade path in [MIGRATION.md](MIGRATION.md).
- **v2.3.0** — Analytics breadth pass, no schema changes. Two new admin endpoints land: `GET /api/admin/analytics/webhooks-health` surfaces total / active / degraded endpoint counts plus a top-10 recent-failures table reading from `WebhookEndpoint.ConsecutiveFailures` + `WebhookDelivery.LastErrorMessage`, so a broken subscriber shows up in the dashboard instead of customer complaints; `GET /api/admin/analytics/by-sender?days=&take=` returns dispatched / completed / voided counts + avg time-to-sign per `SigningRequest.CreatedBy`, with an order-by-dispatched cap. The funnel response gains a `byDayBucket` field carrying Mon-Fri UTC vs Sat-Sun UTC slices with a narrative "weekday converts 12 pp higher than weekend" line on the dashboard (with a "no signal" fallback for gaps under 5 pp). New `GET /api/admin/audit/export` streams the audit trail as RFC 4180 CSV with `from` / `to` / `eventType` / `actorUserId` filters, configurable `Stampd:Admin:MaxAuditExportRows` cap (default 250k), self-describing filename. Dashboard gets two new sections — "Webhook health" between analytics and the danger zone, and "Audit-trail export" with two native `<a download>` buttons for "last N days" and "all (capped)" exports.
- **v2.2.0** — Analytics deepening pass, no schema changes. Every `/api/admin/analytics/*` endpoint now runs its aggregation twice per call (current window + equally-sized prior window) and the `/admin` dashboard renders ▲/▼ delta chips next to every headline metric (green for improvement, red for regression, `lowerIsBetter` flipping the sense for time-to-sign and lockout count). New per-channel identity-verification breakdown: stacked bars for OTP initiates and lockouts split into Email / SMS / KBA — Email and SMS classified from the OTP identifier shape, KBA pulled from `Recipient.IdentityVerificationMethod` with a footnote calling out its proxied initiate count. Funnel gains a request-weighted view alongside the recipient-level numbers: each SigningRequest contributes `signed ÷ recipients` to the average, so a 2-of-3-signed request scores 0.67 instead of being read as binary completed/not — fairer metric for tenants running multi-recipient workflows. Time-to-sign gains per-role segmentation aggregating across templates by `Recipient.Role.Name` ("(unassigned)" fallback for legacy data) so admins see whether "Approvers" or "Witnesses" systematically take longer at the tenant level. All new response fields are nullable — pre-v2.2 Stampd.UI against v2.2 WebApi (or vice versa) deserializes cleanly.
- **v2.1.0** — Transparent signature rendering: signature/initial PNGs with alpha now render with their transparent background preserved (was a black rectangle on v2.0 due to PdfSharp 6.x alpha handling — fixed via a SkiaSharp re-encode pass before `XImage.FromStream`). Strict completed-date sort on `GET /api/signing-requests?sortBy=completed`: V15 migration adds a nullable `CompletedAtUtcEpochMs` shadow column on SigningRequest with a covering `(TenantId, CompletedAtUtcEpochMs)` index on SQLite + SqlServer + Postgres, replacing v2.0's "route through `CreatedAtUtcEpochMs`" workaround. URL query-string sync on `/designer/requests`: filter / sort / page state mirrors into the address bar so links are shareable and the browser back button restores prior views. Opt-in DigiCert TSA failover: new `Stampd:Tsa:EnableDigiCertFailover` config flag (default `false`) wraps the primary TSA in a `FailoverTimestampAuthorityProvider` that falls back to DigiCert's free public TSA on FreeTSA timeouts. The standalone bring-your-own-certificate sample (`samples/Stampd.ByoCertDemo`) ships with the failover always on plus a file-based logger writing to `~/.stampd/byo-cert-demo/logs/demo-*.log` for demo diagnostics.
- **v2.0.0** — Real RBAC across the API and UI (Admin / Sender / ReadOnly roles, ASP.NET Core authorization policies, role-gated nav, "logged in as" topbar). Admin dashboard at `/admin`: summary tiles for live request states, 30-day trend SVG, top-templates table, analytics section with drop-off funnel + per-template time-to-sign + identity-verification metrics on a shared window selector (7 / 30 / 90 days). Signing-requests list gets a filter panel (status, sender email, recipient email, dispatched-from/to) and explicit sort selector. Admin bulk operations: void selected, resend invitations, demo cleanup tile. Every `AuditEvent` now carries `ActorUserId` + `ActorRole` (V14 migration on SQLite / SqlServer / Postgres + composite `(TenantId, ActorUserId, OccurredAtUtc)` index). UI polish: theme choice now survives Blazor enhanced-navigation via head-inline restore + `enhancedload` re-apply + MutationObserver guard; ghost / primary / icon-button hover text legibility fixed across both themes; signer signature canvas pinned to white so dark-mode ink is visible.
- **v1.3.0** — Strict-ETSI B-LTA hardening (`id-aa-ats-hash-index-v3` imprint + parallel `/Type /DocTimeStamp` PAdES Part 4 carrier), sender completion notification email + executive-grade invitation HTML upgrade, multi-recipient sender detail page at `/designer/requests/{id}` with audit timeline, OTP rate limit + brute-force lockout, paginated signing-requests list with prev/next + per-page selector, epoch sort columns finished on SigningRequest + SignedDocumentRecord plus four catch-up migrations bringing SqlServer + Postgres providers to V13.
- **v1.2.0** — Strict ETSI B-LT via PDF incremental update, B-LTA archive timestamp (CMS-attribute form), Email-OTP identity-verification UI gate, edit-existing-template flow, server-side worker sort columns, seamless `/demo` one-click bootstrap, sender-side **Requests** view with sealed-PDF download, executive-grade HTML OTP email template, auto-auth in Development (zero terminal commands, zero copy-paste).
- **v1.1.0** — Production sealing providers, multi-cloud storage, B-LT signatures, webhooks, bulk-send, full Blazor signer + designer UI. End-to-end verified.
- **v1.0.0** — PAdES B-B + B-T engine, local certificate sealing, Vault HSM via BYOK, multi-tenant API, EF Core 10.

## Comparison

|  | DocuSign | DocuSeal | Documenso | **Stampd** |
|---|---|---|---|---|
| **License** | Proprietary | AGPL + paid Pro | AGPL + paid EE | **Apache 2.0 only** |
| **Stack** | Closed | Ruby on Rails | TypeScript / Next.js | **C# / .NET 10** |
| **Pricing** | Per envelope | Free + Pro tier | Free + EE tier | **Free forever** |
| **HSM-backed signing** | Managed only | Cert in app | Cert in app | **Vault, KV, KMS** |
| **Self-hosted** | ❌ | ✅ | ✅ | ✅ |
| **Native .NET integration** | REST only | REST only | JS embed | **NuGet + Blazor RCL** |
| **Database flexibility** | Managed | Postgres only | Postgres only | **SQL Server, Postgres, SQLite** |
| **TSA flexibility** | Managed | One URL | One URL | **Pluggable provider** |

## Contributing

We're early. The biggest help right now is using Stampd in a real project and telling us what's broken, missing, or surprising.

- **Issues** — bug reports, feature requests, design discussions
- **Pull requests** — code changes, doc fixes, new provider implementations
- **Discussions** — design ideas, architecture questions, "is this the right way" check-ins

All contributions land under the Apache 2.0 license. By submitting a PR you're certifying you have the right to license your contribution under those terms (DCO).

## License

Stampd is licensed under the **Apache License, Version 2.0**. See [LICENSE](LICENSE).

This applies to the entire codebase — engine, providers, infrastructure, WebApi, UI, and tools. There is no separate "Enterprise" tier and no relicensing clause.

---

<div align="center">

**Built by .NET developers, for .NET developers.**
If Stampd saves your team from per-envelope billing, [tell us about it](https://github.com/isureshsubramanian/Stampd/discussions) — it's the kind of validation that keeps an open-source project alive.

</div>
