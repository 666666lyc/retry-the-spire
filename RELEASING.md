# Release process

Retry the Spire uses semantic version tags in the form `vX.Y.Z`. Version gaps
are allowed; do not create a release unless its source commit and distributable
artifact can both be identified.

## Checklist

1. Update the version in `RetryTheSpire.csproj`, `mod_manifest.json`,
   `RetryMod.cs`, `README.md`, and `INSTALL.md`.
2. Build against a local Slay the Spire 2 installation and stage the package
   under the ignored `dist/` directory.
3. Verify that the archive contains only `RetryTheSpire.dll`,
   `mod_manifest.json`, `LICENSE`, and `INSTALL.md` inside the
   `RetryTheSpire/` directory.
4. Generate `SHA256SUMS.txt` with one line in this format:

   ```text
   <sha256>  RetryTheSpire-vX.Y.Z.zip
   ```

5. Commit source and documentation only. Never commit release ZIP files,
   generated binaries, staging directories, or recovery files.
6. Create an annotated `vX.Y.Z` tag whose message and GitHub Release title are
   `Retry the Spire vX.Y.Z`.
7. Write the GitHub Release notes in Chinese first and English second, then
   upload exactly the product ZIP and `SHA256SUMS.txt`.
8. Confirm the tag target, archive checksum, package layout, and GitHub
   `Latest` marker after publishing.
