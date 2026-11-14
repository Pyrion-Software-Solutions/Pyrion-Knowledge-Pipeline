# Contributing to Pyrion Knowledge Pipeline

Contributions to Persian/English document preparation, extraction correctness, accessibility, tests, and documentation are welcome. Check existing issues and open an issue before proposing a large change.

## Development setup

1. Fork the repository and create a branch from `main`.
2. Install the .NET SDK specified in `global.json`. Supported use is on Windows.
3. From the repository root, run:

```powershell
dotnet restore PyrionKnowledgePipeline.sln
dotnet build PyrionKnowledgePipeline.sln --configuration Release --no-restore
dotnet test PyrionKnowledgePipeline.sln --configuration Release --no-build
.\scripts\run-app.ps1
```

The standard tests use synthetic fixtures and mocked model responses. They do not require LM Studio or PostgreSQL. Two optional checks require local services: `PYRION_TEST_DATABASE` enables the disposable PostgreSQL/pgvector integration test, and `PYRION_RUN_LM_SMOKE=1` plus an explicit `PYRION_EVAL_MODEL` enables the LM Studio evaluation. See [index setup](docs/knowledge-index-setup.md) and [retrieval evaluation](docs/retrieval-evaluation.md). Report skipped checks accurately.

## Project boundaries

- Preserve original source text, tables, source references, and the explicit human approval gate.
- Keep the application and model/database endpoints on loopback. Public hosting, authentication, and multi-user operation require a separate design.
- Keep uploaded documents, local settings, connection strings, model responses, and generated exports out of Git. Use synthetic, redistributable fixtures in reports and tests.
- Keep Domain independent of Infrastructure and Web. See [architecture](docs/architecture.md).

## Pull requests

Describe the problem, resulting behavior, and validation. Include regression coverage for behavioral fixes, and screenshots for visible changes where useful. Use focused commits, retain existing code style, and update relevant documentation. Never claim checks passed unless they were run; list optional services that were unavailable.

Discuss ideas respectfully and provide actionable review feedback. Report security concerns using [SECURITY.md](SECURITY.md), rather than a public issue. Contributions are submitted under the project's [MIT License](LICENSE).
