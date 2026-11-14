# Preparing a release

The release workflow prepares a **draft prerelease** for maintainer review. It does not publish the release automatically.

## Local validation

From Windows with the SDK specified by `global.json`:

```powershell
dotnet test PyrionKnowledgePipeline.sln --configuration Release
.\scripts\package-release.ps1 -Version 0.1.0-preview.1
```

The version above is an example. Choose a unique version for the actual release. The script refuses an existing output directory to prevent stale files entering a package. Outputs are under `artifacts/releases/<version>`: a self-contained Windows x64 ZIP, SHA-256 checksums, and the unpackaged app. The ZIP includes the MIT license, sample DOCX, launch script, and usage notes. LM Studio, model files, PostgreSQL, and pgvector are separate prerequisites for the features that use them.

## GitHub draft

After this workflow is merged into `main`, open Actions → Draft Windows release → Run workflow. Select `main` and enter a unique version. The workflow builds and runs baseline tests, packages the app, uploads an Actions artifact, and creates a draft prerelease targeting the selected commit. Optional PostgreSQL and live-model tests are skipped unless separately exercised in a controlled local environment.

Before publishing the draft:

- Download and verify the ZIP's checksum, then extract it into a clean Windows folder.
- Follow `RELEASE-README.md`, including sample extraction, local model analysis, human approval, and JSON download.
- Confirm static assets and the sample download work; test restart with isolated synthetic storage.
- Run the optional database and model checks for any feature whose release notes claim that coverage.
- Replace generated notes with verified changes, limitations, and prerequisites; attach a screenshot containing only synthetic data if helpful.
- Review dependencies, licensing, and any known security issues. Publish the draft only when these checks support the release claims.

The workflow uses the repository's scoped GitHub token; no personal access token or OpenAI API key is needed. A failed run can leave an existing draft; inspect it before retrying the same version.
