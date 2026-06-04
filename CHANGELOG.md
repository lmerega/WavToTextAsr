# Changelog

All notable changes to this project are documented in this file.

## [1.0.1] - 2026-06-04

### Fixed
- Added in-memory WAV PCM normalization before Google Speech-to-Text requests so 24-bit and 32-bit PCM `.wav` files are accepted more reliably.
- Fixed single-file publish localization extraction so interface language changes also update button captions in the published executable.
- Completed missing UI translation keys in the non-English resource files so labels and buttons now switch language correctly for French, Arabic, Chinese, German, and the other translated interfaces.

### Changed
- Updated package and assembly version to `1.0.1`.

## [1.0.0] - 2026-05-29

### Added
- Initial Windows desktop release for batch audio transcription with Google Cloud Speech-to-Text.
