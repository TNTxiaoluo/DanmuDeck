# Release conventions

- Unless the user explicitly says to prepare a Microsoft Store release, treat builds and changes as testing work. Test versions display a lowercase `b` suffix, for example `1.0.2b`.
- A Store release displays the version requested by the user without `b`. The current requested Store release is `1.0.2` (`1.0.2.0` in the package manifest).
- Appx/MSIX package identity versions must remain numeric. Keep development package versions increasing under the development identity; their internal install version may differ from the displayed release version.
- Use the build scripts to isolate test and Store identity/version changes. Reuse the existing development signing certificate; keep signing keys and maintainer Store association files out of public source exports.
- Preserve testing history in the changelog and distinguish it from published Store releases. A generated upload container is not evidence that a version was published.
