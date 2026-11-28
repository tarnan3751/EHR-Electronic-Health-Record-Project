# Release notes

One file per GitHub release, named by its tag (`v1.0.0.md`). `.github/workflows/release.yml` refuses to release a tag
without its file here, and publishes the file as it is as the release's notes, with the commit it was built from added
at the end. Its links are absolute, to the repository at the tag, since a release's page resolves no relative link.

To release a version:

1. On a branch, set it in `src/Ehr.Desktop/src-tauri/Cargo.toml` (`cargo build` updates its entry in `Cargo.lock`), and
   add its notes here.
2. Rehearse: `gh workflow run ci.yml --ref <branch> -f release_dry_run=true` builds everything the release would and
   keeps the installers, checksums and notes as the run's artifacts for 7 days; nothing is pushed or released.
3. Once it's merged, tag the commit on main and push the tag: `git tag v<version> <commit>`, then
   `git push origin v<version>`. release.yml checks the tag, builds the installers and the images, pushes the images
   as `<version>` and `latest`, and makes the release.

A release signs no update: an update is made on the owner's Mac (`docs/desktop-install.md`, "Making a release").
