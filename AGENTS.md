# Repository guidance

## Purpose and boundaries

Pyrion Knowledge Pipeline is a .NET 10 Blazor Interactive Server application for local Persian/English engineering-document preparation. Preserve source content and references, and require explicit human approval before producing approved normalized JSON. JSON exports are structured data, not a trained model.

Keep the application, LM Studio, and optional PostgreSQL/pgvector endpoints on loopback. Do not add public hosting, cloud calls, remote fonts, or bypasses for host checks. Never read or commit users' local documents, credentials, or settings as fixtures.

## Build and verify

Run from the repository root with the SDK specified in `global.json`:

```powershell
dotnet restore PyrionKnowledgePipeline.sln
dotnet build PyrionKnowledgePipeline.sln --configuration Release --no-restore
dotnet test PyrionKnowledgePipeline.sln --configuration Release --no-build
```

The baseline tests mock LM Studio. PostgreSQL and live embedding checks are opt-in; do not enable them against a user's services without explicit task authorization. Record skipped tests rather than reporting them as verified. `scripts/run-app.ps1` stops processes occupying port 5187; use an isolated port and storage root for automated smoke checks instead.

## Change conventions

Keep Domain independent; Web composes Application and Infrastructure. Add meaningful regression coverage for behavior changes and update the relevant guide. Do not rewrite historical validation results in `docs/validation.md` to imply they describe a new run. Use synthetic, redistributable fixtures. See `CONTRIBUTING.md`, `SECURITY.md`, and `docs/architecture.md`.
