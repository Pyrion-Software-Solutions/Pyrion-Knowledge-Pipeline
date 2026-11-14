# Run the Windows release

This archive is a self-contained Windows x64 build. The .NET SDK and Microsoft Word are not required to run it. It is an unsigned application; verify the release source and checksum before running it.

1. Extract the entire ZIP to a folder you can write to. Do not run it inside the ZIP viewer.
2. From PowerShell in that folder, run `./Start-Pyrion.ps1`. If your script policy does not permit it, run `./PyrionKnowledgePipeline.Web.exe --urls http://localhost:5187` directly from the extracted folder.
3. Open <http://localhost:5187> after the server starts. Keep the terminal open; Ctrl+C stops the app. Stop an existing Pyrion instance before starting another.
4. Start LM Studio separately, load a chat-capable model, and enable its loopback server. In Pyrion's LM Studio Settings, test the endpoint and select a model.
5. In Document Preparation, download the included synthetic sample, fill sample metadata, upload it, and inspect extraction. Request analysis, review the proposal, and explicitly approve it before downloading approved JSON.

Extraction and review do not require a database. AI analysis requires LM Studio. Knowledge indexing/retrieval additionally requires local PostgreSQL with pgvector and a separate embedding model. See the repository's README and index setup guide for configuration.

Documents and settings are stored under `%LOCALAPPDATA%\PyrionKnowledgePipeline` by default, outside the extracted folder. Back up this directory before upgrading. Do not expose the app over a public interface, proxy, or tunnel: it is a single-user local application without authentication. Use synthetic documents for demonstrations.

To verify an archive, compare `Get-FileHash ./PyrionKnowledgePipeline-<version>-win-x64.zip -Algorithm SHA256` with the accompanying `SHA256SUMS.txt`. A matching checksum confirms consistency with the published file; it is not a code signature.
