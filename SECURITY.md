# Security policy

Pyrion is a single-user application intended to run on localhost. It has no user authentication and must not be exposed through a public interface, proxy, or tunnel. LM Studio and the optional PostgreSQL database must also be local. Stored documents and settings are not encrypted by the application; protect them with operating-system permissions.

## Supported code

Security fixes currently target `main`. There is no maintained older-release support policy yet. Check the repository's published releases and update to a release containing any relevant fix when one is available.

## Report a vulnerability privately

Use GitHub's [private vulnerability reporting](https://github.com/Pyrion-Software-Solutions/Pyrion-Knowledge-Pipeline/security/advisories/new) if it is enabled. If that option is unavailable, contact a repository maintainer through the contact information on their GitHub profile to request a private reporting channel. Do not publish exploit details or sensitive documents in an issue.

Include the affected commit or release, a description of the impact, reproduction steps using synthetic files, and any proposed mitigation. Remove credentials, personal information, and customer or engineering documents from attachments and logs. No response-time or bounty commitment is currently offered.

Until a fix is available, stop using the affected workflow if it could compromise documents or the local system. Report dependencies and parser failures as well as application vulnerabilities.
